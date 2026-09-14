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
		}

		public static sbyte SignForTransmitter(string nt)
		{
			switch (nt.Trim().ToLowerInvariant()) {
				case "gaba":
				case "glutamate":
				case "glut":
				case "histamine":
				case "hist":
					return -1;
				default:
					return 1;
			}
		}
	}
}
