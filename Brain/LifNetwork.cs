using System;
using System.Collections.Generic;

namespace FlyRarria.Brain
{
	/// <summary>
	/// Current-based leaky integrate-and-fire network, Shiu et al. 2024 parameters.
	/// tau_m dv/dt = (v_rest - v) + g; tau_s dg/dt = -g;
	/// presynaptic spike (after delay): g += sign * N * weightMv * gain.
	/// Silent unless driven: no spontaneous activity, every spike traces to a stimulus.
	///
	/// Event-driven so a whole CNS (176k neurons) fits the game tick. Each step only
	/// touches awake neurons: stimulated, refractory, or with max(v - rest, g) at or above
	/// threshold - rest. Anything else can't reach threshold without new input (each step
	/// v - rest becomes a blend of itself and g, and g only decays, so it never exceeds the
	/// larger of the two), so it sleeps, and its next input first catches it up with the
	/// closed form of the same per-step update. Same spikes as stepping every neuron.
	/// Not thread-safe: one thread at a time may call into it.
	/// </summary>
	public sealed class LifNetwork
	{
		public struct Params
		{
			public double TauMms;
			public double TauSms;
			public double RestMv;
			public double ThresholdMv;
			public double RefractoryMs;
			public double DelayMs;
			public double WeightMvPerSynapse;
			public double Gain;

			public static Params Shiu2024(float gain = 0.65f) => new Params {
				TauMms = 20.0,
				TauSms = 5.0,
				RestMv = -52.0,
				ThresholdMv = -45.0,
				RefractoryMs = 2.2,
				DelayMs = 1.8,
				WeightMvPerSynapse = 0.275,
				Gain = gain,
			};
		}

		/// <summary>
		/// Everything a synaptic delivery touches, packed together: deliveries land on
		/// random neurons, so each one costs a single cache line instead of one per array.
		/// </summary>
		private struct Cell
		{
			public double V;
			public double G;
			public double RefractoryLeft;
			/// <summary>While asleep, V and G are as of the start of this step.</summary>
			public int SyncStep;
			public bool Awake;
			/// <summary>Has a Poisson rate (in <see cref="_poissonHz"/>) and a place in <see cref="_stimulated"/>.</summary>
			public bool Stimulated;
		}

		/// <summary>A neuron asleep this many steps (4 s at 0.5 ms) has decayed to rest in double precision.</summary>
		private const int CatchUpSteps = 8192;
		/// <summary>Rates below this are zeroed so the estimate list stays short (and out of denormals).</summary>
		private const double RateFloorHz = 1e-9;

		private readonly Connectome _graph;
		private readonly Params _p;
		private readonly Cell[] _cells;
		private readonly double[] _poissonHz;
		private readonly int[] _windowSpikes;
		private readonly double[] _rateEma;
		private readonly bool[] _isRated;
		private readonly Random _rng;
		private int _step;

		private readonly double _decayV;
		private readonly double _decayG;
		private readonly double _stepSec;
		private readonly int _delaySteps;
		/// <summary>Sleep once max(v - rest, g) is below this (threshold - rest, less rounding slack).</summary>
		private readonly double _sleepBelowMv;
		// k input-free steps: v - rest -> _powV[k] (v - rest) + _gToV[k] g, g -> _powG[k] g.
		private readonly double[] _powV;
		private readonly double[] _powG;
		private readonly double[] _gToV;

		// Neurons stepped this step, each once (Cell.Awake).
		private readonly int[] _active;
		private int _activeCount;
		// Stimulated neurons in index order, so Poisson draws come in a fixed order.
		private readonly List<int> _stimulated = new List<int>();
		private bool _stimulatedDirty;
		// Neurons that spiked, waiting out the synaptic delay: a ring of one bucket per step.
		private readonly List<int>[] _pending;
		private readonly List<int> _spikesThisTick = new List<int>();
		// Neurons with a nonzero rate estimate.
		private readonly List<int> _rated = new List<int>();

		public double DtMs { get; }
		/// <summary>Every spike during the last <see cref="Step"/> call (a neuron may repeat).</summary>
		public IReadOnlyList<int> SpikesThisTick => _spikesThisTick;
		/// <summary>Neurons stepped at the end of the last step; the rest were asleep.</summary>
		public int AwakeCount => _activeCount;

		public LifNetwork(Connectome graph, double dtMs, Params p, int? seed = null)
		{
			_graph = graph;
			_p = p;
			DtMs = dtMs;
			_rng = seed.HasValue ? new Random(seed.Value) : new Random();
			int n = graph.NeuronCount;
			_cells = new Cell[n];
			_poissonHz = new double[n];
			_windowSpikes = new int[n];
			_rateEma = new double[n];
			_isRated = new bool[n];
			_active = new int[n];

			_decayV = Math.Exp(-dtMs / p.TauMms);
			_decayG = Math.Exp(-dtMs / p.TauSms);
			_stepSec = dtMs / 1000.0;
			_delaySteps = Math.Max(1, (int)Math.Round(p.DelayMs / dtMs));
			_sleepBelowMv = p.ThresholdMv - p.RestMv - 1e-6;
			_pending = new List<int>[_delaySteps];
			for (int i = 0; i < _delaySteps; i++) {
				_pending[i] = new List<int>();
			}

			_powV = new double[CatchUpSteps];
			_powG = new double[CatchUpSteps];
			_gToV = new double[CatchUpSteps];
			_powV[0] = 1;
			_powG[0] = 1;
			for (int k = 1; k < CatchUpSteps; k++) {
				_powV[k] = _powV[k - 1] * _decayV;
				_powG[k] = _powG[k - 1] * _decayG;
				_gToV[k] = _gToV[k - 1] * _decayV + _powG[k - 1] * (1.0 - _decayV);
			}
			Reset();
		}

		public void Reset()
		{
			Array.Clear(_cells);
			for (int i = 0; i < _cells.Length; i++) {
				_cells[i].V = _p.RestMv;
			}
			Array.Clear(_poissonHz);
			Array.Clear(_windowSpikes);
			Array.Clear(_rateEma);
			Array.Clear(_isRated);
			_activeCount = 0;
			_stimulated.Clear();
			_stimulatedDirty = false;
			foreach (var bucket in _pending) {
				bucket.Clear();
			}
			_rated.Clear();
			_spikesThisTick.Clear();
			_step = 0;
		}

		public void SetStimulusRate(int neuron, double hz)
		{
			ref Cell c = ref _cells[neuron];
			bool now = hz > 0;
			_poissonHz[neuron] = hz;
			if (now == c.Stimulated) {
				return;
			}
			c.Stimulated = now;
			_stimulatedDirty = true;
			if (now) {
				_stimulated.Add(neuron);
				Wake(ref c, neuron);
			}
		}

		public void ClearStimuli()
		{
			// Only SetStimulusRate writes rates, so the list covers every nonzero one.
			// The neurons stay awake and fall asleep on their own.
			foreach (int i in _stimulated) {
				_poissonHz[i] = 0;
				_cells[i].Stimulated = false;
			}
			_stimulated.Clear();
			_stimulatedDirty = false;
		}

		/// <summary>Advance <paramref name="steps"/> integration steps and update the rate estimates.</summary>
		public void Step(int steps)
		{
			_spikesThisTick.Clear();
			if (_stimulatedDirty) {
				_stimulated.Sort();
				int kept = 0;
				for (int k = 0; k < _stimulated.Count; k++) {
					int i = _stimulated[k];
					if (_cells[i].Stimulated && (kept == 0 || _stimulated[kept - 1] != i)) {
						_stimulated[kept++] = i;
					}
				}
				_stimulated.RemoveRange(kept, _stimulated.Count - kept);
				_stimulatedDirty = false;
			}

			Cell[] cells = _cells;
			int[] rowStart = _graph.RowStart;
			int[] targets = _graph.Targets;
			ushort[] counts = _graph.SynapseCounts;
			sbyte[] signs = _graph.Signs;
			int neuronCount = _graph.NeuronCount;
			double restMv = _p.RestMv;

			for (int s = 0; s < steps; s++, _step++) {
				// Deliver due synaptic input, catching sleeping targets up first.
				var due = _pending[_step % _delaySteps];
				foreach (int pre in due) {
					double dv = signs[pre] * _p.WeightMvPerSynapse * _p.Gain;
					int rowEnd = pre + 1 < neuronCount ? rowStart[pre + 1] : targets.Length;
					for (int e = rowStart[pre]; e < rowEnd; e++) {
						int target = targets[e];
						ref Cell c = ref cells[target];
						if (!c.Awake) {
							Wake(ref c, target);
						}
						c.G += dv * counts[e];
					}
				}
				due.Clear();

				// Poisson sensory drive (as in the paper), drawn in index order.
				foreach (int i in _stimulated) {
					ref Cell c = ref cells[i];
					if (_rng.NextDouble() < _poissonHz[i] * _stepSec) {
						Fire(ref c, i);
					}
					else {
						Integrate(ref c, i);
					}
				}

				int awake = 0;
				for (int a = 0; a < _activeCount; a++) {
					int i = _active[a];
					ref Cell c = ref cells[i];
					if (c.Stimulated) {
						_active[awake++] = i; // already stepped above
						continue;
					}
					Integrate(ref c, i);
					if (c.RefractoryLeft > 0 || Math.Max(c.V - restMv, c.G) >= _sleepBelowMv) {
						_active[awake++] = i;
					}
					else {
						c.Awake = false;
						c.SyncStep = _step + 1;
					}
				}
				_activeCount = awake;
			}

			// EMA of each neuron's rate over this window. Counts every spike in the
			// window, so rates are real Hz like tools/bench.py and the decoder thresholds.
			double alpha = 1.0 - Math.Exp(-steps * DtMs / 150.0);
			double windowSec = steps * _stepSec;
			foreach (int i in _spikesThisTick) {
				if (_windowSpikes[i]++ == 0 && !_isRated[i]) {
					_isRated[i] = true;
					_rated.Add(i);
				}
			}
			int stillRated = 0;
			for (int r = 0; r < _rated.Count; r++) {
				int i = _rated[r];
				double inst = _windowSpikes[i] / windowSec;
				_windowSpikes[i] = 0;
				_rateEma[i] += alpha * (inst - _rateEma[i]);
				if (_rateEma[i] < RateFloorHz) {
					_rateEma[i] = 0;
					_isRated[i] = false;
				}
				else {
					_rated[stillRated++] = i;
				}
			}
			_rated.RemoveRange(stillRated, _rated.Count - stillRated);
		}

		public double RateHz(int neuron) => _rateEma[neuron];

		public double MeanRateHz(int[] population)
		{
			if (population.Length == 0) {
				return 0;
			}
			double sum = 0;
			for (int k = 0; k < population.Length; k++) {
				sum += _rateEma[population[k]];
			}
			return sum / population.Length;
		}

		private void Integrate(ref Cell c, int i)
		{
			if (c.RefractoryLeft > 0) {
				c.RefractoryLeft -= DtMs;
				return;
			}
			c.V = _p.RestMv + (c.V - _p.RestMv) * _decayV + c.G * (1.0 - _decayV);
			c.G *= _decayG;
			if (c.V >= _p.ThresholdMv) {
				Fire(ref c, i);
			}
		}

		private void Wake(ref Cell c, int i)
		{
			if (c.Awake) {
				return;
			}
			int k = _step - c.SyncStep;
			if (k >= CatchUpSteps) {
				c.V = _p.RestMv;
				c.G = 0;
			}
			else if (k > 0) {
				c.V = _p.RestMv + _powV[k] * (c.V - _p.RestMv) + _gToV[k] * c.G;
				c.G *= _powG[k];
			}
			c.Awake = true;
			_active[_activeCount++] = i;
		}

		private void Fire(ref Cell c, int i)
		{
			c.V = _p.RestMv;
			c.G = 0;
			c.RefractoryLeft = _p.RefractoryMs;
			_spikesThisTick.Add(i);
			// Delivered _delaySteps from now, which is this same ring slot.
			_pending[_step % _delaySteps].Add(i);
		}
	}
}
