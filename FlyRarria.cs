using System.Collections.Generic;
using Terraria.ModLoader;
using FlyRarria.Brain;

namespace FlyRarria
{
	public class FlyRarria : Mod
	{
		public override void Load()
		{
			// tML's compiler ignores <EmbeddedResource>, so Circuits/*.json ship as loose
			// .tmod files. Cache them while the archive is open; CircuitLoader reads them
			// whenever a mote is summoned.
			var circuits = new Dictionary<string, byte[]>();
			var names = GetFileNames();
			if (names != null) {
				foreach (string path in names) {
					if (path.StartsWith("Circuits/") && path.EndsWith(".json")) {
						circuits[path] = GetFileBytes(path);
					}
				}
			}
			CircuitLoader.FileSource = path => circuits.TryGetValue(path, out var bytes) ? bytes : null;
		}

		public override void Unload()
		{
			CircuitLoader.FileSource = null;
		}
	}
}
