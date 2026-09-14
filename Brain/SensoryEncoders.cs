using System;
using System.Collections.Generic;

namespace FlyRarria.Brain
{
	/// <summary>
	/// Turns a SensoryFrame into Poisson firing rates on real sensory types.
	/// Same saturating law as fly-brain-minecraft: rate = rMax * S^1.5 / (0.2^1.5 + S^1.5),
	/// so encounters (onsets) matter more than steady levels. Side-specific channels
	/// drive the ipsilateral population, which is what gives steering its direction.
	/// </summary>
	public static class SensoryEncoders
	{
		public static double Saturate(double s, double rMax)
		{
			double p = Math.Pow(Math.Max(0, s), 1.5);
			double c = Math.Pow(0.2, 1.5);
			return rMax * p / (c + p);
		}

		public static List<(string type, string side, double hz)> Encode(SensoryFrame f)
		{
			var drives = new List<(string, string, double)>();
			void Add(string type, float s, double rMax, string side = null)
			{
				if (s > 0.01f) {
					drives.Add((type, side, Saturate(s, rMax)));
				}
			}

			// Vision: analytic feature channels (the LIF medulla stays silent, same
			// documented limitation as the Minecraft mod — drive LC/LPLC directly).
			Add("LC4", f.LoomLeft, 150, "L");
			Add("LC4", f.LoomRight, 150, "R");
			Add("LPLC2", f.LoomLeft, 150, "L");
			Add("LPLC2", f.LoomRight, 150, "R");
			Add("LC10a", f.ChaseLeft, 120, "L");
			Add("LC10a", f.ChaseRight, 120, "R");
			Add("LC11", f.SmallObjectLeft, 100, "L");
			Add("LC11", f.SmallObjectRight, 100, "R");

			// Taste: contact only.
			Add("LB3b", f.SugarContact, 120);
			Add("LB3c", f.SugarContact, 120);
			Add("PhG1a", f.SugarContact, 100);
			Add("LgLG3", f.SugarContact, 80);
			Add("LB1a", f.BitterContact, 120);
			Add("LB1b", f.BitterContact, 120);

			// Smell: food-ester glomeruli prime approach; valence resolved by taste.
			Add("ORN_DM1", f.FoodSmell, 60);
			Add("ORN_VA2", f.FoodSmell, 60);

			// Mechano: wind splits by side; touch/damage drive bristles + grooming JO.
			Add("prefix:JO-C", f.WindLeft, 120, "L"); // JO-C/E: static antennal deflection (wind)
			Add("prefix:JO-E", f.WindLeft, 120, "L");
			Add("prefix:JO-C", f.WindRight, 120, "R");
			Add("prefix:JO-E", f.WindRight, 120, "R");
			Add("JO-FV", f.Touch, 120);
			Add("BM_InOm", f.Touch, 100);
			Add("BM_InOm", f.DamageFlash, 200);

			// Thermo + social flavor.
			Add("TRN_VP2", f.Heat, 100);
			Add("ORN_VA1v", f.SocialCue, 60);

			return drives;
		}

		public static void Apply(LifNetwork net, PopulationIndex pops, List<(string type, string side, double hz)> drives)
		{
			net.ClearStimuli();
			foreach (var (type, side, hz) in drives) {
				foreach (int n in pops.Resolve(type, side)) {
					net.SetStimulusRate(n, hz);
				}
			}
		}
	}
}
