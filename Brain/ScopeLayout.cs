using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace FlyRarria.Brain
{
	/// <summary>
	/// The brain silhouette the neuroscope draws, baked by tools/scope_shape.py from the
	/// male-cns ROI meshes: region fills as pixel row spans in a Width x Height grid, plus
	/// the projection that maps connectome voxel positions into that grid. The brain is a
	/// frontal view seen from behind (the fly's left on the left, dorsal up) and the VNC
	/// hangs below it as a dorsal view. Engine-independent.
	/// </summary>
	public sealed class ScopeShape
	{
		public readonly int Width, Height;
		/// <summary>Pixel row where the VNC starts, for captions.</summary>
		public readonly float VncTop;
		/// <summary>Per region ("rind", "central", "optic", "vnc", "neuropil", "surface"), flat (row, col, length) spans.</summary>
		public readonly Dictionary<string, int[]> Spans = new Dictionary<string, int[]>();
		private readonly double _xMax, _yMin, _scale, _pad, _zNeck, _vncZMin, _vncScale;

		private ScopeShape(JsonElement root)
		{
			Width = root.GetProperty("width").GetInt32();
			Height = root.GetProperty("height").GetInt32();
			var p = root.GetProperty("projection");
			_xMax = p.GetProperty("x_max").GetDouble();
			_yMin = p.GetProperty("y_min").GetDouble();
			_scale = p.GetProperty("scale").GetDouble();
			_pad = p.GetProperty("pad").GetDouble();
			_zNeck = p.GetProperty("z_neck").GetDouble();
			_vncZMin = p.GetProperty("vnc_z_min").GetDouble();
			_vncScale = p.GetProperty("vnc_scale").GetDouble();
			VncTop = (float)p.GetProperty("vnc_top").GetDouble();
			foreach (var region in root.GetProperty("regions").EnumerateObject()) {
				var flat = new int[region.Value.GetArrayLength() * 3];
				int k = 0;
				foreach (var span in region.Value.EnumerateArray()) {
					flat[k++] = span[0].GetInt32();
					flat[k++] = span[1].GetInt32();
					flat[k++] = span[2].GetInt32();
				}
				Spans[region.Name] = flat;
			}
		}

		/// <summary>Loads Circuits/scope_shape.json, or null with the reason in <paramref name="note"/>.</summary>
		public static ScopeShape TryLoad(out string note)
		{
			using Stream s = CircuitLoader.OpenData("scope_shape.json");
			if (s == null) {
				note = "no Circuits/scope_shape.json: run tools/scope_shape.py";
				return null;
			}
			using var doc = JsonDocument.Parse(s);
			if (doc.RootElement.GetProperty("format").GetString() != "flyraria-scope-shape-v1") {
				note = "Circuits/scope_shape.json is not flyraria-scope-shape-v1";
				return null;
			}
			note = null;
			return new ScopeShape(doc.RootElement);
		}

		/// <summary>Voxel position -> pixel. Mirrors project() in tools/scope_shape.py.</summary>
		public (float u, float v) Project(float x, float y, float z)
		{
			double u = _pad + (_xMax - x) * _scale;
			double v = z < _zNeck ? _pad + (y - _yMin) * _scale : VncTop + (z - _vncZMin) * _scale * _vncScale;
			return ((float)u, (float)v);
		}
	}

	/// <summary>
	/// 2D placement of every neuron for the neuroscope overlay, in normalized 0..1
	/// coordinates of a <see cref="ScopeShape"/>: each neuron sits where it is in the fly
	/// (<see cref="Connectome.Positions"/>), projected the same way as the silhouette.
	/// Engine-independent.
	/// </summary>
	public sealed class ScopeLayout
	{
		public const int EdgesPerNeuron = 4;

		public readonly Connectome Graph;
		public readonly ScopeShape Shape;
		public readonly float[] X;
		public readonly float[] Y;
		/// <summary>Neurons in a <see cref="MotorDecoder.Readouts"/> population (drawn larger).</summary>
		public readonly bool[] IsReadout;
		/// <summary>
		/// Per neuron, <see cref="EdgesPerNeuron"/> slots holding indices into
		/// <see cref="Connectome.Targets"/> of its strongest outgoing synapses, -1 padded.
		/// Neuron of slot k is k / EdgesPerNeuron.
		/// </summary>
		public readonly int[] TopEdges;

		/// <summary>Null when the circuits carry no positions or the shape is missing; the reason goes to <paramref name="note"/>.</summary>
		public static ScopeLayout TryCreate(Connectome graph, PopulationIndex pops, out string note)
		{
			if (graph.Positions == null) {
				note = "circuit files have no neuron positions: run tools/extract_circuits.py --positions-only";
				return null;
			}
			var shape = ScopeShape.TryLoad(out note);
			return shape == null ? null : new ScopeLayout(graph, pops, shape);
		}

		private ScopeLayout(Connectome graph, PopulationIndex pops, ScopeShape shape)
		{
			Graph = graph;
			Shape = shape;
			int n = graph.NeuronCount;
			X = new float[n];
			Y = new float[n];
			for (int i = 0; i < n; i++) {
				var (u, v) = shape.Project(graph.Positions[i * 3], graph.Positions[i * 3 + 1], graph.Positions[i * 3 + 2]);
				X[i] = Math.Clamp(u / shape.Width, 0f, 1f);
				Y[i] = Math.Clamp(v / shape.Height, 0f, 1f);
			}

			IsReadout = new bool[n];
			foreach (var (type, side) in MotorDecoder.Readouts) {
				foreach (int i in pops.Resolve(type, side)) {
					IsReadout[i] = true;
				}
			}

			TopEdges = new int[n * EdgesPerNeuron];
			Array.Fill(TopEdges, -1);
			for (int i = 0; i < n; i++) {
				int rowEnd = i + 1 < n ? graph.RowStart[i + 1] : graph.Targets.Length;
				int slot0 = i * EdgesPerNeuron;
				for (int e = graph.RowStart[i]; e < rowEnd; e++) {
					if (graph.Targets[e] == i) {
						continue;
					}
					// Insertion into the neuron's small sorted slot list, strongest first.
					for (int s = slot0; s < slot0 + EdgesPerNeuron; s++) {
						if (TopEdges[s] < 0 || graph.SynapseCounts[e] > graph.SynapseCounts[TopEdges[s]]) {
							Array.Copy(TopEdges, s, TopEdges, s + 1, slot0 + EdgesPerNeuron - s - 1);
							TopEdges[s] = e;
							break;
						}
					}
				}
			}
		}

		/// <summary>Mean position of a population, or null if it resolves to nothing.</summary>
		public (float x, float y)? Centroid(int[] population)
		{
			if (population.Length == 0) {
				return null;
			}
			float x = 0, y = 0;
			foreach (int i in population) {
				x += X[i];
				y += Y[i];
			}
			return (x / population.Length, y / population.Length);
		}
	}
}
