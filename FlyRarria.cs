using System.Collections.Generic;
using Terraria.ModLoader;
using FlyRarria.Brain;

namespace FlyRarria
{
	public class FlyRarria : Mod
	{
		public override void Load()
		{
			// tML's compiler ignores <EmbeddedResource>, so Circuits/* (the circuit JSON and the
			// whole-CNS connectome) ship as loose .tmod files. Cache them while the archive is
			// open; CircuitLoader reads them when the first mote is summoned.
			var circuits = new Dictionary<string, byte[]>();
			var names = GetFileNames();
			if (names != null) {
				foreach (string path in names) {
					if (path.StartsWith("Circuits/") && (path.EndsWith(".json") || path.EndsWith(".gz"))) {
						circuits[path] = GetFileBytes(path);
					}
				}
			}
			CircuitLoader.FileSource = path => circuits.TryGetValue(path, out var bytes) ? bytes : null;
		}

		public override void Unload()
		{
			CircuitLoader.FileSource = null;
			CircuitLoader.ForgetShared();
		}
	}
}
