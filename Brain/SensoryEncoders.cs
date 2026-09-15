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

		/// <summary>
		/// Sugar GRN gain from hunger (0 full .. 1 starving), as starvation raises
		/// sugar-neuron sensitivity in real flies. 1.0 at 0.2 (a new bond's default and
		/// SensoryFrame.Empty), so the bench-verified sugar response is unchanged there.
		/// Steep below that: depression in the whole CNS compresses MN9, so a full fly needs
		/// 0.2 to stay near silent (MN9 ~14 Hz, vs ~26 Hz at the old 0.6, which still fed).
		/// </summary>
		public static double SugarGain(float hunger) => Math.Min(1.5, 0.2 + 4.0 * Math.Clamp(hunger, 0f, 1f));
		/// <summary>
		/// Ambient-light gate on vision (flies are diurnal): 1.0 in daylight down to
		/// 0.2 in the dark. Escape still fires at night — the giant-fiber path is a
		/// hair-trigger by design — but tracking (chase, small objects) weakens a lot.
		/// Gives the sampled-but-unused LightLevel a real effect.
		/// </summary>
		public static double VisionGain(float light) => 0.2 + 0.8 * Math.Clamp(light, 0f, 1f);

		/// <summary>A frame with every channel on, so <see cref="Encode"/> lists every population it can drive.</summary>
		public static SensoryFrame EveryChannel => new SensoryFrame {
			LoomLeft = 1, LoomRight = 1, ChaseLeft = 1, ChaseRight = 1, SmallObjectLeft = 1, SmallObjectRight = 1,
			SugarContact = 1, BitterContact = 1, FoodSmellLeft = 1, FoodSmellRight = 1, WindLeft = 1, WindRight = 1, Touch = 1,
			DamageFlash = 1, LightLevel = 1, Heat = 1, SocialCue = 1, Hunger = 1,
		};

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
		// Gated by ambient light: the fly sees full contrast by day, little at night.
		double vision = VisionGain(f.LightLevel);
		Add("LC4", f.LoomLeft, 150 * vision, "L");
		Add("LC4", f.LoomRight, 150 * vision, "R");
		Add("LPLC1", f.LoomLeft, 150 * vision, "L");
		Add("LPLC1", f.LoomRight, 150 * vision, "R");
		Add("LPLC2", f.LoomLeft, 150 * vision, "L");
		Add("LPLC2", f.LoomRight, 150 * vision, "R");
		Add("LC10a", f.ChaseLeft, 120 * vision, "L");
		Add("LC10a", f.ChaseRight, 120 * vision, "R");
		Add("LC11", f.SmallObjectLeft, 100 * vision, "L");
		Add("LC11", f.SmallObjectRight, 100 * vision, "R");

			// Taste: contact only. Hunger turns sugar sensing up, satiety turns it down;
			// whether that is enough to drive MN9 past the feed threshold is the circuit's call.
			double sweet = SugarGain(f.Hunger);
			Add("LB3b", f.SugarContact, 120 * sweet);
			Add("LB3c", f.SugarContact, 120 * sweet);
		Add("PhG1a", f.SugarContact, 100 * sweet);
		Add("PhG1b", f.SugarContact, 100 * sweet);
		Add("PhG1c", f.SugarContact, 100 * sweet);
		Add("LgLG3", f.SugarContact, 80 * sweet);
		Add("LgLG4", f.SugarContact, 80 * sweet);
		Add("LB1a", f.BitterContact, 120);
		Add("LB1b", f.BitterContact, 120);
		Add("LB1c", f.BitterContact, 120);
		Add("LB1d", f.BitterContact, 120);

		// Smell: food-ester glomeruli, split by side so the brain knows which way
		// dinner is. Weighted by hunger like sugar: a full fly barely smells food,
		// so satiety-gated seeking falls out without the decoder knowing hunger.
		double smell = SugarGain(f.Hunger);
		Add("ORN_DM1", f.FoodSmellLeft, 60 * smell, "L");
		Add("ORN_DM1", f.FoodSmellRight, 60 * smell, "R");
		Add("ORN_VA2", f.FoodSmellLeft, 60 * smell, "L");
		Add("ORN_VA2", f.FoodSmellRight, 60 * smell, "R");

		// Mechano: wind splits by side; touch/damage drive bristles + grooming JO.
		// JO-CM is the only JO-C* type in male-cns (there is no JO-E*), so wind
		// drives it alone: a 30-neuron population that primes grooming (~24-35Hz
		// on the groom DNs in a storm, usually over the 30Hz threshold) but only
		// really grooms together with rain or damage.
		Add("prefix:JO-C", f.WindLeft, 120, "L"); // static antennal deflection (wind)
		Add("prefix:JO-C", f.WindRight, 120, "R");
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
