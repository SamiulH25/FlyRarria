using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace FlyRarria.Brain
{
	/// <summary>
	/// Loads tools/extract_circuits.py output (flyraria-circuit-v1 JSON) into a
	/// merged Connectome. Returns null when no circuit data is embedded yet —
	/// callers must keep the documented [reflex] fallback in that case.
	/// </summary>
	public static class CircuitLoader
	{
		private static readonly string[] Circuits = { "escape", "steer", "feed", "groom", "song" };

		public static bool TriedLoad { get; private set; }
		public static string LoadNote { get; private set; } = "circuit data not loaded";

		/// <summary>
		/// Fallback when the JSON isn't an embedded resource: returns a file's bytes by
		/// path ("Circuits/steer.json") or null. tML's in-game compiler ignores the csproj's
		/// EmbeddedResource, so in the mod the circuits ship as loose .tmod files and
		/// FlyRarria.Load wires this up; MSBuild harnesses still use the embedded copy.
		/// </summary>
		public static Func<string, byte[]> FileSource { get; set; }

		/// <summary>Opens Circuits/<paramref name="file"/> from the embedded resources or <see cref="FileSource"/>; null if neither has it.</summary>
		public static Stream OpenData(string file)
		{
			return Assembly.GetExecutingAssembly().GetManifestResourceStream($"FlyRarria.Circuits.{file}")
				?? (FileSource?.Invoke($"Circuits/{file}") is byte[] bytes ? new MemoryStream(bytes) : null);
		}

		private static readonly object SharedLock = new object();
		private static Connectome _shared;
		private static bool _sharedIsWholeCns;
		private static bool _sharedTried;

		/// <summary>
		/// The graph every mote runs: the whole CNS when its file ships, else the merged
		/// circuit JSON, else null ([reflex]). Loaded once and shared, since the whole CNS
		/// takes ~0.6 s and ~160 MB; blocks, so call it off the game thread.
		/// </summary>
		/// <param name="parameters">What to run it with: <see cref="LifNetwork.Params.WholeCns"/> or <see cref="LifNetwork.Params.Shiu2024"/>.</param>
		public static Connectome LoadShared(out LifNetwork.Params parameters)
		{
			lock (SharedLock) {
				if (!_sharedTried) {
					_shared = TryLoadWholeCns();
					_sharedIsWholeCns = _shared != null;
					_shared ??= TryLoadAll();
					_sharedTried = true;
				}
				parameters = _sharedIsWholeCns ? LifNetwork.Params.WholeCns() : LifNetwork.Params.Shiu2024();
				return _shared;
			}
		}

		public static void ForgetShared()
		{
			lock (SharedLock) {
				_shared = null;
				_sharedIsWholeCns = false;
				_sharedTried = false;
			}
		}

		/// <summary>Circuits/male-cns.connectome.gz (tools/extract_connectome.py), or null when it doesn't ship.</summary>
		public static Connectome TryLoadWholeCns()
		{
			TriedLoad = true;
			using Stream s = OpenData(ConnectomeFile.FileName);
			if (s == null) {
				LoadNote = $"no Circuits/{ConnectomeFile.FileName}";
				return null;
			}
			var clock = System.Diagnostics.Stopwatch.StartNew();
			Connectome graph = ConnectomeFile.Read(s, out string dataset, out _);
			LoadNote = $"whole CNS ({dataset}): {graph.NeuronCount:N0} neurons, {graph.Targets.Length:N0} connections, loaded in {clock.ElapsedMilliseconds} ms";
			return graph;
		}

		public static Connectome TryLoadAll()
		{
			TriedLoad = true;
			var neurons = new List<(int body, string type, string nt, double x, double y, float[] pos)>();
			var edges = new List<(int pre, int post, int count)>();
			// Circuits share neurons (and so edges): index bodies across all files and
			// keep each pre->post edge once, so shared cells and synapses aren't doubled.
			var indexByBody = new Dictionary<long, int>();
			var seenEdges = new HashSet<(int, int)>();

			foreach (string name in Circuits) {
				using Stream s = OpenData($"{name}.json");
				if (s == null) {
					continue;
				}
				using var doc = JsonDocument.Parse(s);
				var root = doc.RootElement;
				if (root.GetProperty("format").GetString() != "flyraria-circuit-v1") {
					continue;
				}
				foreach (var n in root.GetProperty("neurons").EnumerateArray()) {
					long body = n.GetProperty("body").GetInt64();
					if (indexByBody.ContainsKey(body)) {
						continue;
					}
					indexByBody[body] = neurons.Count;
					neurons.Add((
						(int)body,
						n.GetProperty("type").GetString() ?? "?",
						n.TryGetProperty("nt", out var nt) ? nt.GetString() ?? "" : "",
						n.TryGetProperty("x", out var x) ? x.GetDouble() : 0,
						n.TryGetProperty("y", out var y) ? y.GetDouble() : 0,
						n.TryGetProperty("pos", out var p) && p.GetArrayLength() == 3
							? new[] { p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle() } : null));
				}
				foreach (var e in root.GetProperty("edges").EnumerateArray()) {
					edges.Add((indexByBody[e[0].GetInt64()], indexByBody[e[1].GetInt64()], e[2].GetInt32()));
				}
			}

			if (neurons.Count == 0) {
				LoadNote = "no Circuits/*.json with neurons (embedded or mod file) — running [reflex] fallback";
				return null;
			}

			int nCount = neurons.Count;
			var rowCounts = new int[nCount];
			foreach (var (pre, _, _) in edges) {
				rowCounts[pre]++;
			}
			var rowStart = new int[nCount];
			int acc = 0;
			for (int i = 0; i < nCount; i++) {
				rowStart[i] = acc;
				acc += rowCounts[i];
			}
			var targets = new int[edges.Count];
			var counts = new ushort[edges.Count];
			var filled = new int[nCount];
			foreach (var (pre, post, c) in edges) {
				int slot = rowStart[pre] + filled[pre]++;
				targets[slot] = post;
				counts[slot] = (ushort)Math.Min(c, ushort.MaxValue);
			}

			var signs = new sbyte[nCount];
			var types = new string[nCount];
			var bodies = new int[nCount];
			var xs = new double[nCount];
			var ys = new double[nCount];
			var positions = new float[nCount * 3];
			for (int i = 0; i < nCount; i++) {
				signs[i] = Connectome.SignForTransmitter(neurons[i].nt);
				types[i] = neurons[i].type;
				bodies[i] = neurons[i].body;
				xs[i] = neurons[i].x;
				ys[i] = neurons[i].y;
				if (positions != null && neurons[i].pos is float[] pos) {
					Array.Copy(pos, 0, positions, i * 3, 3);
				}
				else {
					positions = null; // all or nothing: a half-placed brain would mislead
				}
			}

			LoadNote = $"{nCount} neurons, {edges.Count} edges from circuit files";
			return new Connectome(nCount, rowStart, targets, counts, signs, types, bodies, xs, ys, positions);
		}
	}
}
