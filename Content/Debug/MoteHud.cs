using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.ModLoader;
using Terraria.UI;
using FlyRarria.Content.Bond;
using FlyRarria.Content.Pets;

namespace FlyRarria.Content.Debug
{
	/// <summary>
	/// Neuroscope-lite: tiny corner readout of the live mode, bond, hunger, and
	/// whether the brain or the [reflex] fallback is driving. Never shows fake
	/// activity — Reflex reads "reflex" until circuit data is embedded.
	/// </summary>
	public class MoteHud : ModSystem
	{
		public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
		{
			int idx = layers.FindIndex(l => l.Name == "Vanilla: Entity Health Bars");
			var layer = new LegacyGameInterfaceLayer("FlyRarria: Mote", DrawMote, InterfaceScaleType.UI);
			if (idx >= 0) {
				layers.Insert(idx, layer);
			}
			else {
				layers.Add(layer);
			}
		}

		private bool DrawMote()
		{
			Player player = Main.LocalPlayer;
			if (player.dead || !player.HasBuff(ModContent.BuffType<MoteBuff>())) {
				return true;
			}
			string mode = "away";
			string drive = "reflex";
			for (int i = 0; i < Main.maxProjectiles; i++) {
				var p = Main.projectile[i];
				if (p.active && p.type == ModContent.ProjectileType<MoteProjectile>()
					&& p.owner == player.whoAmI && p.ModProjectile is MoteProjectile m) {
					mode = m.CurrentMode.ToString().ToLower();
					drive = m.BrainReflex ? "reflex" : "brain";
				}
			}
			var bond = BondSystem.Instance?.Get(player);
			string text = $"mote:{mode} [{drive}] bond:Lv{bond?.Level} fed:{bond?.Hunger:F0}";
			var sb = Main.spriteBatch;
			var font = FontAssets.MouseText.Value;
			Vector2 pos = new Vector2(20, Main.screenHeight - 60);
			sb.DrawString(font, text, pos + new Vector2(2, 2), Color.Black * 0.6f);
			sb.DrawString(font, text, pos, Color.LightYellow);
			return true;
		}
	}
}
