using System;
using System.Collections.Generic;

namespace FlyRarria.Brain
{
	/// <summary>
	/// Current-based leaky integrate-and-fire network, Shiu et al. 2024 parameters.
	/// tau_m dv/dt = (v_rest - v) + g; tau_s dg/dt = -g;
	/// presynaptic spike (after delay): g += sign * N * weightMv * gain.
	/// Silent unless driven: no spontaneous activity, every spike traces to a stimulus.
	/// Sized for extracted circuits (hundreds to low thousands of neurons), so a
	/// straightforward dense-state loop is fast enough to step inline on the game tick.
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

		private readonly Connectome _graph;
		private readonly Params _p;
		private readonly double[] _v;
		private readonly double[] _g;
		private readonly double[] _refractoryLeft;
		private readonly double[] _poissonHz;
		private readonly Queue<(int target, double dg, int dueStep)> _delayed;
		private readonly List<int> _spikesThisTick = new List<int>();
		private readonly double[] _rateEma;
		private readonly Random _rng = new Random();
		private int _step;

		public double DtMs { get; }
		public IReadOnlyList<int> SpikesThisTick => _spikesThisTick;

		public LifNetwork(Connectome graph, double dtMs, Params p)
		{
			_graph = graph;
			_p = p;
			DtMs = dtMs;
			_v = new double[graph.NeuronCount];
			_g = new double[graph.NeuronCount];
			_refractoryLeft = new double[graph.NeuronCount];
			_poissonHz = new double[graph.NeuronCount];
			_rateEma = new double[graph.NeuronCount];
			_delayed = new Queue<(int, double, int)>();
			Reset();
		}

		public void Reset()
		{
			for (int i = 0; i < _graph.NeuronCount; i++) {
				_v[i] = _p.RestMv;
				_g[i] = 0;
				_refractoryLeft[i] = 0;
				_poissonHz[i] = 0;
				_rateEma[i] = 0;
			}
			_delayed.Clear();
			_step = 0;
		}

		public void SetStimulusRate(int neuron, double hz) => _poissonHz[neuron] = hz;

		public void ClearStimuli() => Array.Clear(_poissonHz, 0, _poissonHz.Length);

		/// <summary>Advance <paramref name="steps"/> integration steps. Returns spikes on the final step.</summary>
		public void Step(int steps)
		{
			_spikesThisTick.Clear();
			double decayV = Math.Exp(-DtMs / _p.TauMms);
			double decayG = Math.Exp(-DtMs / _p.TauSms);
			int delaySteps = Math.Max(1, (int)Math.Round(_p.DelayMs / DtMs));
			double stepSec = DtMs / 1000.0;

			for (int s = 0; s < steps; s++, _step++) {
				// Deliver due synaptic input.
				while (_delayed.Count > 0 && _delayed.Peek().dueStep <= _step) {
					var (target, dg, _) = _delayed.Dequeue();
					_g[target] += dg;
				}

				for (int i = 0; i < _graph.NeuronCount; i++) {
					// Poisson sensory drive (as in the paper).
					if (_poissonHz[i] > 0 && _rng.NextDouble() < _poissonHz[i] * stepSec) {
						Fire(i, delaySteps, s == steps - 1);
						continue;
					}
					if (_refractoryLeft[i] > 0) {
						_refractoryLeft[i] -= DtMs;
						continue;
					}
					_v[i] = _p.RestMv + (_v[i] - _p.RestMv) * decayV + _g[i] * (1.0 - decayV);
					_g[i] *= decayG;
					if (_v[i] >= _p.ThresholdMv) {
						Fire(i, delaySteps, s == steps - 1);
					}
				}
			}

			// EMA rates over the final-step window for the decoder.
			double alpha = 1.0 - Math.Exp(-steps * DtMs / 150.0);
			var counted = new HashSet<int>(_spikesThisTick);
			for (int i = 0; i < _graph.NeuronCount; i++) {
				double inst = counted.Contains(i) ? 1.0 / (steps * stepSec) : 0.0;
				_rateEma[i] += alpha * (inst - _rateEma[i]);
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

		private void Fire(int i, int delaySteps, bool record)
		{
			_v[i] = _p.RestMv;
			_g[i] = 0;
			_refractoryLeft[i] = _p.RefractoryMs;
			if (record) {
				_spikesThisTick.Add(i);
			}
			double dv = _graph.Signs[i] * _p.WeightMvPerSynapse * _p.Gain;
			int rowEnd = i + 1 < _graph.NeuronCount ? _graph.RowStart[i + 1] : _graph.Targets.Length;
			for (int e = _graph.RowStart[i]; e < rowEnd; e++) {
				_delayed.Enqueue((_graph.Targets[e], dv * _graph.SynapseCounts[e], _step + delaySteps));
			}
		}
	}
}
