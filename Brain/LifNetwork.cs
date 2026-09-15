using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

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
	///
	/// Multi-core: neurons are split into shards of contiguous indices, each owned by one
	/// thread. A spike reaches its targets only after the synaptic delay, so within a block
	/// of that many steps no shard needs anything another shard computes in the same block:
	/// the shards run each block in parallel and meet in between. Every shard delivers the
	/// spikes due to its own neurons in ascending presynaptic order and Poisson draws are a
	/// hash of (seed, body id, step), so the spikes don't depend on the number of shards.
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
			/// <summary>
			/// Short-term depression: the share of a neuron's synaptic resources each spike uses up,
			/// 0 for none. A spike transmits at the fraction still available, which recovers toward 1
			/// with <see cref="DepressionTauMs"/> (Tsodyks-Markram, no facilitation). Poisson-driven
			/// sensory neurons are exempt: the encoders already set their rates.
			/// </summary>
			public double DepressionU;
			public double DepressionTauMs;

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

			/// <summary>
			/// The whole male CNS: <see cref="Shiu2024"/> at gain 0.8 plus short-term depression
			/// (U 0.05, 300 ms recovery). Without depression, loops the extracted circuits leave out
			/// (antennal-lobe local neurons, Kenyon cells) latch at their top rate after one odour or
			/// taste and never stop. Depression caps what a sustained high rate passes on while onsets
			/// still get through. Picked by sweeping U 0.03-0.2 and gain 0.55-2.4 against the
			/// behaviours the decoder reads; gain 0.9 makes a full fly feed, U 0.08 loses feeding.
			/// One excitatory antennal-lobe loop (lLN1_bc, lLN2T/X) still holds ~5k spikes a step
			/// after chemosensory input; escape, feeding, grooming and steering all read through it.
			/// </summary>
			public static Params WholeCns()
			{
				Params p = Shiu2024(0.8f);
				p.DepressionU = 0.05;
				p.DepressionTauMs = 300;
				return p;
			}
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
			/// <summary>Has a Poisson rate (in <see cref="_poissonHz"/>) and a place in its shard's Stimulated list.</summary>
			public bool Stimulated;
		}

		/// <summary>Neurons [Start, End) and the state only the thread stepping them may touch during a block.</summary>
		private sealed class Shard
		{
			public int Start, End;
			/// <summary>This shard's words in the spike bitmaps (shards start on multiples of 64).</summary>
			public int WordStart, WordEnd;
			/// <summary>Neurons stepped this step, each once (Cell.Awake).</summary>
			public int[] Active;
			public int ActiveCount;
			/// <summary>Stimulated neurons in index order.</summary>
			public readonly List<int> Stimulated = new List<int>();
			public bool StimulatedDirty;
			public readonly List<int> Spikes = new List<int>();
			/// <summary>Spikes this shard has written into each ring slot since clearing its part of it.</summary>
			public int[] RingSpikes;
		}

		/// <summary>A neuron asleep this many steps (4 s at 0.5 ms) has decayed to rest in double precision.</summary>
		private const int CatchUpSteps = 8192;
		/// <summary>Rates below this are zeroed so the estimate list stays short (and out of denormals).</summary>
		private const double RateFloorHz = 1e-9;
		private const int MaxShards = 64;

		private readonly Connectome _graph;
		private readonly Params _p;
		private readonly Cell[] _cells;
		private readonly double[] _poissonHz;
		private readonly int[] _windowSpikes;
		private readonly double[] _rateEma;
		private readonly bool[] _isRated;
		private readonly ulong _seed;
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

		private readonly Shard[] _shards;
		private readonly byte[] _shardOf;
		/// <summary>
		/// Rows are sorted by target, so each neuron's connections into shard k are one run:
		/// edges [_rowSplit[i * (S + 1) + k], _rowSplit[i * (S + 1) + k + 1]).
		/// </summary>
		private readonly int[] _rowSplit;
		/// <summary>
		/// Spike bitmaps, one per step, in a ring twice the synaptic delay long: step s writes
		/// slot s and reads slot s - delay, so a block never reads a slot it writes.
		/// </summary>
		private readonly ulong[][] _ring;
		private readonly ParallelOptions _parallel;
		private readonly Action<int> _runShard;
		private int _blockStart;
		private int _blockSteps;

		// Short-term depression, when on: resources left after each neuron's last spike, that
		// spike's step, and the efficacy of every spike in flight, per ring slot like the bitmaps.
		private readonly double[] _resource;
		private readonly int[] _lastFireStep;
		private readonly double[][] _ringEfficacy;
		/// <summary>exp(-k dt / tau_rec): resources recover as 1 - (1 - left) * this after k steps.</summary>
		private readonly double[] _recover;

		private readonly List<int> _spikesThisTick = new List<int>();
		// Neurons with a nonzero rate estimate.
		private readonly List<int> _rated = new List<int>();

		public double DtMs { get; }
		public int ShardCount => _shards.Length;
		/// <summary>Every spike during the last <see cref="Step"/> call (a neuron may repeat).</summary>
		public IReadOnlyList<int> SpikesThisTick => _spikesThisTick;
		/// <summary>Neurons stepped at the end of the last step; the rest were asleep.</summary>
		public int AwakeCount {
			get {
				int n = 0;
				foreach (var sh in _shards) {
					n += sh.ActiveCount;
				}
				return n;
			}
		}

		/// <param name="shards">Threads stepping the network (capped so each shard gets a few words of neurons).</param>
		public LifNetwork(Connectome graph, double dtMs, Params p, int? seed = null, int shards = 1)
		{
			_graph = graph;
			_p = p;
			DtMs = dtMs;
			_seed = seed.HasValue ? (ulong)seed.Value : (ulong)Random.Shared.NextInt64();
			int n = graph.NeuronCount;
			_cells = new Cell[n];
			_poissonHz = new double[n];
			_windowSpikes = new int[n];
			_rateEma = new double[n];
			_isRated = new bool[n];

			_decayV = Math.Exp(-dtMs / p.TauMms);
			_decayG = Math.Exp(-dtMs / p.TauSms);
			_stepSec = dtMs / 1000.0;
			_delaySteps = Math.Max(1, (int)Math.Round(p.DelayMs / dtMs));
			_sleepBelowMv = p.ThresholdMv - p.RestMv - 1e-6;

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

			_shards = MakeShards(graph, Math.Clamp(shards, 1, MaxShards));
			_shardOf = new byte[n];
			foreach (var sh in _shards) {
				Array.Fill(_shardOf, (byte)Array.IndexOf(_shards, sh), sh.Start, sh.End - sh.Start);
			}
			_rowSplit = SplitRows(graph, _shards);
			int words = (n + 63) >> 6;
			_ring = new ulong[2 * _delaySteps][];
			for (int i = 0; i < _ring.Length; i++) {
				_ring[i] = new ulong[words];
			}
			foreach (var sh in _shards) {
				sh.RingSpikes = new int[_ring.Length];
			}
			if (p.DepressionU > 0) {
				_resource = new double[n];
				_lastFireStep = new int[n];
				_ringEfficacy = new double[_ring.Length][];
				for (int i = 0; i < _ring.Length; i++) {
					_ringEfficacy[i] = new double[n];
				}
				_recover = new double[CatchUpSteps];
				for (int k = 0; k < CatchUpSteps; k++) {
					_recover[k] = Math.Exp(-k * dtMs / p.DepressionTauMs);
				}
			}
			_parallel = new ParallelOptions { MaxDegreeOfParallelism = _shards.Length };
			_runShard = RunShard;
			Reset();
		}

		/// <summary>
		/// Contiguous shards holding roughly equal numbers of incoming connections (what
		/// deliveries cost), plus one per neuron for stepping it, each starting on a bitmap word.
		/// </summary>
		private static Shard[] MakeShards(Connectome graph, int wanted)
		{
			int n = graph.NeuronCount;
			wanted = Math.Max(1, Math.Min(wanted, (n + 1023) / 1024));
			var load = new long[n];
			foreach (int t in graph.Targets) {
				load[t]++;
			}
			long total = 0;
			for (int i = 0; i < n; i++) {
				total += load[i] + 1;
			}
			var starts = new List<int> { 0 };
			long acc = 0;
			for (int i = 0; i < n && starts.Count < wanted; i++) {
				acc += load[i] + 1;
				int next = (i + 64) & ~63; // first word boundary after neuron i
				if (acc * wanted >= total * starts.Count && next < n && next > starts[^1]) {
					starts.Add(next);
				}
			}
			var shards = new Shard[starts.Count];
			for (int k = 0; k < shards.Length; k++) {
				int start = starts[k], end = k + 1 < starts.Count ? starts[k + 1] : n;
				shards[k] = new Shard {
					Start = start, End = end,
					WordStart = start >> 6, WordEnd = (end + 63) >> 6,
					Active = new int[end - start],
				};
			}
			return shards;
		}

		private static int[] SplitRows(Connectome graph, Shard[] shards)
		{
			int n = graph.NeuronCount, stride = shards.Length + 1;
			int[] rowStart = graph.RowStart, targets = graph.Targets;
			var split = new int[n * stride];
			for (int i = 0; i < n; i++) {
				int e = rowStart[i], end = i + 1 < n ? rowStart[i + 1] : targets.Length;
				split[i * stride] = e;
				for (int k = 1; k < shards.Length; k++) {
					while (e < end && targets[e] < shards[k].Start) {
						e++;
					}
					split[i * stride + k] = e;
				}
				split[i * stride + shards.Length] = end;
			}
			return split;
		}

		public void Reset()
		{
			Array.Clear(_cells);
			for (int i = 0; i < _cells.Length; i++) {
				_cells[i].V = _p.RestMv;
			}
			Array.Clear(_poissonHz);
			if (_resource != null) {
				Array.Fill(_resource, 1.0);
				Array.Clear(_lastFireStep);
			}
			Array.Clear(_windowSpikes);
			Array.Clear(_rateEma);
			Array.Clear(_isRated);
			foreach (var bits in _ring) {
				Array.Clear(bits);
			}
			foreach (var sh in _shards) {
				sh.ActiveCount = 0;
				sh.Stimulated.Clear();
				sh.StimulatedDirty = false;
				sh.Spikes.Clear();
				Array.Clear(sh.RingSpikes);
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
			Shard sh = _shards[_shardOf[neuron]];
			sh.StimulatedDirty = true;
			if (now) {
				sh.Stimulated.Add(neuron);
				Wake(sh, ref c, neuron, _step);
			}
		}

		public void ClearStimuli()
		{
			// Only SetStimulusRate writes rates, so the lists cover every nonzero one.
			// The neurons stay awake and fall asleep on their own.
			foreach (var sh in _shards) {
				foreach (int i in sh.Stimulated) {
					_poissonHz[i] = 0;
					_cells[i].Stimulated = false;
				}
				sh.Stimulated.Clear();
				sh.StimulatedDirty = false;
			}
		}

		/// <summary>Advance <paramref name="steps"/> integration steps and update the rate estimates.</summary>
		public void Step(int steps)
		{
			_spikesThisTick.Clear();
			foreach (var sh in _shards) {
				sh.Spikes.Clear();
				if (sh.StimulatedDirty) {
					sh.Stimulated.Sort();
					int kept = 0;
					for (int k = 0; k < sh.Stimulated.Count; k++) {
						int i = sh.Stimulated[k];
						if (_cells[i].Stimulated && (kept == 0 || sh.Stimulated[kept - 1] != i)) {
							sh.Stimulated[kept++] = i;
						}
					}
					sh.Stimulated.RemoveRange(kept, sh.Stimulated.Count - kept);
					sh.StimulatedDirty = false;
				}
			}

			for (int done = 0; done < steps;) {
				if (IsQuiet()) {
					// Nothing awake, nothing in flight: skip ahead, sleepers catch up on waking.
					_step += steps - done;
					break;
				}
				_blockStart = _step;
				_blockSteps = Math.Min(_delaySteps, steps - done);
				if (_shards.Length == 1) {
					RunShard(0);
				}
				else {
					Parallel.For(0, _shards.Length, _parallel, _runShard);
				}
				_step += _blockSteps;
				done += _blockSteps;
			}

			foreach (var sh in _shards) {
				_spikesThisTick.AddRange(sh.Spikes);
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

		private bool IsQuiet()
		{
			foreach (var sh in _shards) {
				if (sh.ActiveCount > 0) {
					return false;
				}
				foreach (int count in sh.RingSpikes) {
					if (count > 0) {
						return false;
					}
				}
			}
			return true;
		}

		/// <summary>Steps one shard through the current block. Touches only its own cells, bitmap words and lists.</summary>
		private void RunShard(int k)
		{
			Shard sh = _shards[k];
			Cell[] cells = _cells;
			int[] targets = _graph.Targets;
			ushort[] counts = _graph.SynapseCounts;
			sbyte[] signs = _graph.Signs;
			int[] split = _rowSplit;
			ulong[] inverted = _graph.InvertedEdges;
			bool[] hasInverted = _graph.HasInvertedEdges;
			int stride = _shards.Length + 1;
			double restMv = _p.RestMv;

			for (int s = _blockStart; s < _blockStart + _blockSteps; s++) {
				int writeSlot = s % _ring.Length;
				int readSlot = (writeSlot + _delaySteps) % _ring.Length;
				ulong[] bits = _ring[writeSlot];
				if (sh.RingSpikes[writeSlot] != 0) {
					Array.Clear(bits, sh.WordStart, sh.WordEnd - sh.WordStart);
					sh.RingSpikes[writeSlot] = 0;
				}

				// Deliver due synaptic input in presynaptic order, catching sleeping targets up first.
				bool due = false;
				foreach (var other in _shards) {
					due |= other.RingSpikes[readSlot] != 0;
				}
				if (due) {
					ulong[] fired = _ring[readSlot];
					double[] efficacy = _ringEfficacy?[readSlot];
					for (int w = 0; w < fired.Length; w++) {
						for (ulong word = fired[w]; word != 0; word &= word - 1) {
							int pre = (w << 6) + BitOperations.TrailingZeroCount(word);
							int e = split[pre * stride + k], end = split[pre * stride + k + 1];
							if (e == end || signs[pre] == 0) {
								continue;
							}
							double dv = signs[pre] * _p.WeightMvPerSynapse * _p.Gain;
							if (efficacy != null) {
								dv *= efficacy[pre];
							}
							bool mixed = hasInverted != null && hasInverted[pre];
							for (; e < end; e++) {
								int target = targets[e];
								ref Cell c = ref cells[target];
								if (!c.Awake) {
									Wake(sh, ref c, target, s);
								}
								double input = dv * counts[e];
								c.G += mixed && (inverted[e >> 6] & (1UL << e)) != 0 ? -input : input;
							}
						}
					}
				}

				// Poisson sensory drive (as in the paper).
				foreach (int i in sh.Stimulated) {
					ref Cell c = ref cells[i];
					if (Uniform(i, s) < _poissonHz[i] * _stepSec) {
						Fire(sh, ref c, i, bits, writeSlot, s);
					}
					else {
						Integrate(sh, ref c, i, bits, writeSlot, s);
					}
				}

				int awake = 0;
				for (int a = 0; a < sh.ActiveCount; a++) {
					int i = sh.Active[a];
					ref Cell c = ref cells[i];
					if (c.Stimulated) {
						sh.Active[awake++] = i; // already stepped above
						continue;
					}
					Integrate(sh, ref c, i, bits, writeSlot, s);
					if (c.RefractoryLeft > 0 || Math.Max(c.V - restMv, c.G) >= _sleepBelowMv) {
						sh.Active[awake++] = i;
					}
					else {
						c.Awake = false;
						c.SyncStep = s + 1;
					}
				}
				sh.ActiveCount = awake;
			}
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

		/// <summary>Uniform [0, 1) draw for one neuron and step: splitmix64 of (seed, body id, step).</summary>
		private double Uniform(int neuron, int step)
		{
			ulong z = Mix(_seed ^ ((ulong)(uint)_graph.BodyIds[neuron] * 0x9E3779B97F4A7C15UL));
			z = Mix(z + (ulong)(uint)step * 0xD1B54A32D192ED03UL);
			return (z >> 11) * (1.0 / (1UL << 53));
		}

		private static ulong Mix(ulong z)
		{
			z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
			z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
			return z ^ (z >> 31);
		}

		private void Integrate(Shard sh, ref Cell c, int i, ulong[] bits, int slot, int step)
		{
			if (c.RefractoryLeft > 0) {
				c.RefractoryLeft -= DtMs;
				return;
			}
			c.V = _p.RestMv + (c.V - _p.RestMv) * _decayV + c.G * (1.0 - _decayV);
			c.G *= _decayG;
			if (c.V >= _p.ThresholdMv) {
				Fire(sh, ref c, i, bits, slot, step);
			}
		}

		private void Wake(Shard sh, ref Cell c, int i, int step)
		{
			if (c.Awake) {
				return;
			}
			int k = step - c.SyncStep;
			if (k >= CatchUpSteps) {
				c.V = _p.RestMv;
				c.G = 0;
			}
			else if (k > 0) {
				c.V = _p.RestMv + _powV[k] * (c.V - _p.RestMv) + _gToV[k] * c.G;
				c.G *= _powG[k];
			}
			c.Awake = true;
			sh.Active[sh.ActiveCount++] = i;
		}

		private void Fire(Shard sh, ref Cell c, int i, ulong[] bits, int slot, int step)
		{
			c.V = _p.RestMv;
			c.G = 0;
			c.RefractoryLeft = _p.RefractoryMs;
			if (_resource != null) {
				double available = 1.0;
				if (!c.Stimulated) {
					int k = step - _lastFireStep[i];
					available = k >= CatchUpSteps ? 1.0 : 1.0 - (1.0 - _resource[i]) * _recover[k];
					_resource[i] = available * (1.0 - _p.DepressionU);
					_lastFireStep[i] = step;
				}
				_ringEfficacy[slot][i] = available;
			}
			sh.Spikes.Add(i);
			// Delivered _delaySteps from now, when that step reads this slot.
			bits[i >> 6] |= 1UL << i;
			sh.RingSpikes[slot]++;
		}
	}
}
