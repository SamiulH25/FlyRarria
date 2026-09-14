using System.Collections.Generic;

namespace FlyRarria.Brain
{
	/// <summary>
	/// Name -> neuron resolution. Backed by Connectome.Types; supports exact type,
	/// prefix match, and "/L" "/R" side restriction by soma X sign. Enough for the
	/// extracted circuits; the full spec grammar (class:/superclass:) can come later.
	/// </summary>
	public sealed class PopulationIndex
	{
		private readonly Connectome _graph;
		private readonly Dictionary<string, int[]> _cache = new Dictionary<string, int[]>();

		public PopulationIndex(Connectome graph) => _graph = graph;

		public int[] Resolve(string type, string side = null)
		{
			string key = type + (side ?? "");
			if (_cache.TryGetValue(key, out var hit)) {
				return hit;
			}
			var list = new List<int>();
			bool prefix = type.StartsWith("prefix:");
			string needle = prefix ? type.Substring(7) : type;
			for (int i = 0; i < _graph.NeuronCount; i++) {
				string t = _graph.Types[i] ?? "";
				bool match = prefix ? t.StartsWith(needle) : t == needle || t.StartsWith(needle + "_");
				if (!match) {
					continue;
				}
				if (side == "L" && _graph.SomaX[i] > 0) {
					continue;
				}
				if (side == "R" && _graph.SomaX[i] < 0) {
					continue;
				}
				list.Add(i);
			}
			var arr = list.ToArray();
			_cache[key] = arr;
			return arr;
		}
	}
}
