using Terraria;
using Terraria.ModLoader;
using FlyRarria.Brain;
using FlyRarria.Content.Bond;
using FlyRarria.Content.Pets;

namespace FlyRarria.Content.Debug
{
	/// <summary>Live brain/pet readout. Usage: /fly stats | /fly senses | /fly scope</summary>
	public class MoteCommands : ModCommand
	{
		public override CommandType Type => CommandType.Chat;
		public override string Command => "fly";
		public override string Usage => "/fly stats | /fly senses | /fly scope";
		public override string Description => "FlyRarria companion readout";

		public override void Action(CommandCaller caller, string input, string[] args)
		{
			Player player = caller.Player;
			var bond = BondSystem.Instance?.Get(player);
			int proj = player.ownedProjectileCounts[ModContent.ProjectileType<MoteProjectile>()];

			if (args.Length == 0 || args[0] == "stats") {
				caller.Reply($"Mote: active={proj} bond=Lv{bond?.Level} xp={bond?.Xp} fed={bond?.Hunger:F0}");
				for (int i = 0; i < Main.maxProjectiles; i++) {
					var p = Main.projectile[i];
					if (p.active && p.type == ModContent.ProjectileType<MoteProjectile>() && p.owner == player.whoAmI && p.ModProjectile is MoteProjectile m) {
						string brain = m.BrainLoading ? " [loading brain...]"
							: m.BrainReflex ? $" [reflex — {CircuitLoader.LoadNote}]"
							: $" [brain] {m.Graph.NeuronCount:N0} neurons, {m.Net.ShardCount} threads, last step {m.LastStepMs:F0} ms, {m.RealTimeFactor * 100:F0}% real time";
						caller.Reply($"mode={m.CurrentMode}" + brain);
					}
				}
				return;
			}
			if (args[0] == "scope") {
				Neuroscope.Visible = !Neuroscope.Visible;
				caller.Reply($"neuroscope {(Neuroscope.Visible ? "on" : "off")} (hotkey: {Neuroscope.KeyName})");
				return;
			}
			if (args[0] == "senses") {
				caller.Reply($"light near mote, rain={Main.raining}, night={!Main.dayTime}, hostiles tracked in 30-tile radius");
				return;
			}
			caller.Reply(Usage);
		}
	}
}
