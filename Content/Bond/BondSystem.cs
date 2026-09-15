using System.Collections.Generic;
using Terraria;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace FlyRarria.Content.Bond
{
	/// <summary>
	/// Bond levels + hunger. Persisted per player. Thresholds intentionally
	/// conservative: low bond means distant, never a punishment.
	/// 1 Shy -> 2 Curious -> 3 Brave -> 4 Partner.
	/// </summary>
	public class BondSystem : ModSystem
	{
		public static BondSystem Instance { get; private set; }

		private readonly Dictionary<string, Bond> _bonds = new Dictionary<string, Bond>();

		public override void Load() => Instance = this;
		public override void Unload() => Instance = null;

		public Bond Get(Player player)
		{
			string key = player.name + "|" + player.whoAmI;
			if (!_bonds.TryGetValue(key, out var bond)) {
				bond = new Bond();
				_bonds[key] = bond;
			}
			return bond;
		}

		public bool IsAsleep(Player player) => !Main.dayTime && Get(player).Level >= 4;

		public override void SaveWorldData(TagCompound tag)
		{
			var list = new List<TagCompound>();
			foreach (var kv in _bonds) {
				list.Add(new TagCompound { ["key"] = kv.Key, ["xp"] = kv.Value.Xp, ["hunger"] = kv.Value.Hunger });
			}
			tag["bonds"] = list;
		}

		public override void LoadWorldData(TagCompound tag)
		{
			_bonds.Clear();
			foreach (var t in tag.GetList<TagCompound>("bonds")) {
				_bonds[t.GetString("key")] = new Bond { Xp = t.GetInt("xp"), Hunger = t.GetFloat("hunger") };
			}
		}
	}

	public class Bond
	{
		public int Xp;
		/// <summary>Satiety: 100 full, 0 starving (the field and save key keep the name "hunger").</summary>
		public float Hunger = 80f;

		/// <summary>Hunger drive for the brain: 0 full .. 1 starving.</summary>
		public float Need => 1f - Hunger / 100f;

		public int Level => Xp >= 900 ? 4 : Xp >= 450 ? 3 : Xp >= 150 ? 2 : 1;
	/// <summary>Trust title shown in the HUD and level-up announcements.</summary>
	public string Name => Level switch { 4 => "Partner", 3 => "Brave", 2 => "Curious", _ => "Shy" };
	/// <summary>Bond XP outside meals: fetching, scares.</summary>
	public void Reward(int xp) => Xp += xp;

		public void Feed(bool sweet)
		{
			Hunger = BondMath.ClampRef(Hunger + (sweet ? 25f : -5f));
			if (sweet) {
				Xp += 5;
			}
		}

		public void SharedScare() => Xp += 2;
		public void Tick(float dtMinutes) => Hunger = BondMath.ClampRef(Hunger - dtMinutes * 1.5f);
	}

	internal static class BondMath
	{
		public static float ClampRef(float v) => v < 0 ? 0 : v > 100 ? 100 : v;
	}
}
