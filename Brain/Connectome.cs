using System;
using System.Collections.Generic;

namespace FlyRarria.Brain
{
	/// <summary>
	/// Thresholded connectome graph in CSR form (pre -> post).
	/// Engine-independent: no Terraria references. Data comes from
	/// tools/extract_circuits.py (neuPrint male-cns:v1.0 / BANC), never hand-written.
	/// </summary>
	public sealed class Connectome
	{
		public readonly int NeuronCount;
		public readonly int[] RowStart;
		public readonly int[] Targets;
		public readonly ushort[] SynapseCounts;
		/// Dale's-law sign per presynaptic neuron: +1 excitatory (ACh, monoamines,
		/// unclear), -1 inhibitory (GABA, glutamate, histamine). Shiu et al. 2024.
		public readonly sbyte[] Signs;
		public readonly string[] Types;
		public readonly int[] BodyIds;
		public readonly double[] SomaX;
		public readonly double[] SomaY;
		/// Where each neuron is in the fly, 3 per neuron (x, y, z in male-cns voxels):
		/// soma, or synapse centroid when the soma is outside the CNS. Only the neuroscope
		/// uses it. Null when the circuit files carry no pos.
		public readonly float[] Positions;

		public Connectome(
			int neuronCount, int[] rowStart, int[] targets, ushort[] synapseCounts,
			sbyte[] signs, string[] types, int[] bodyIds, double[] somaX, double[] somaY,
			float[] positions = null)
		{
			Positions = positions;
			NeuronCount = neuronCount;
			RowStart = rowStart;
			Targets = targets;
			SynapseCounts = synapseCounts;
			Signs = signs;
			Types = types;
			BodyIds = bodyIds;
			SomaX = somaX;
			SomaY = somaY;
			SortRows();
			MarkInvertedEdges();
		}

		/// <summary>
		/// Orders each neuron's connections by target (in place), which LifNetwork relies on to
		/// find a neuron's connections into each of its shards. Files from extract_connectome.py
		/// are already sorted; circuit JSON isn't.
		/// </summary>
		private void SortRows()
		{
			for (int i = 0; i < NeuronCount; i++) {
				int start = RowStart[i], end = i + 1 < NeuronCount ? RowStart[i + 1] : Targets.Length;
				for (int e = start + 1; e < end; e++) {
					if (Targets[e] < Targets[e - 1]) {
						Array.Sort(Targets, SynapseCounts, start, end - start);
						break;
					}
				}
			}
		}

		/// <summary>
		/// Fast synaptic sign of a transmitter: GABA, glutamate and histamine inhibit, and
		/// acetylcholine (and unknown or unclear calls) excite, as in Shiu et al. 2024.
		/// Dopamine, octopamine and serotonin act through G-protein receptors on seconds, not
		/// as fast synapses, so they get 0. Treated as excitation, the dopaminergic PAM and PPL
		/// neurons close a KC -> DAN -> KC loop that keeps the mushroom body firing for good.
		/// </summary>
		public static sbyte SignForTransmitter(string nt)
		{
			switch (nt.Trim().ToLowerInvariant()) {
				case "gaba":
				case "glutamate":
				case "glut":
				case "histamine":
				case "hist":
					return -1;
				case "dopamine":
				case "octopamine":
				case "serotonin":
					return 0;
				default:
					return 1;
			}
		}

		/// <summary>
		/// Connections whose sign is the opposite of their presynaptic neuron's, one bit per
		/// connection (bit e of word e / 64), or null when there are none: Kenyon cell -> Kenyon
		/// cell. KCs are cholinergic, but their axo-axonic synapses onto each other act through
		/// muscarinic mAChR-B and suppress the neighbour (Manoim et al. 2022, lateral inhibition).
		/// As excitation, ~1.15M of them make all ~4,000 KCs of the whole CNS fire together for good.
		/// </summary>
		public ulong[] InvertedEdges { get; private set; }
		/// <summary>Neurons with at least one bit set in <see cref="InvertedEdges"/>.</summary>
		public bool[] HasInvertedEdges { get; private set; }

		public bool IsInverted(int edge) => InvertedEdges != null && (InvertedEdges[edge >> 6] & (1UL << edge)) != 0;

		private static bool IsKenyonCell(string type) => type != null && type.StartsWith("KC");

		private void MarkInvertedEdges()
		{
			for (int i = 0; i < NeuronCount; i++) {
				if (!IsKenyonCell(Types[i])) {
					continue;
				}
				int end = i + 1 < NeuronCount ? RowStart[i + 1] : Targets.Length;
				for (int e = RowStart[i]; e < end; e++) {
					if (IsKenyonCell(Types[Targets[e]])) {
						InvertedEdges ??= new ulong[(Targets.Length + 63) >> 6];
						HasInvertedEdges ??= new bool[NeuronCount];
						InvertedEdges[e >> 6] |= 1UL << e;
						HasInvertedEdges[i] = true;
					}
				}
			}
		}
	}
}
