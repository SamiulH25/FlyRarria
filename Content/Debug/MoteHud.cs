using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.ModLoader;
using Terraria.UI;
using FlyRarria.Brain;
using FlyRarria.Content.Bond;
using FlyRarria.Content.Pets;

namespace FlyRarria.Content.Debug
{
	/// <summary>
	/// Neuroscope-lite: tiny panel above the fly showing the live mode, bond,
	/// hunger, and whether the brain or the [reflex] fallback is driving. Never
	/// shows fake activity — Reflex reads "reflex" until circuit data is embedded.
	/// </summary>
	public class MoteHud : ModSystem
	{
		private const float TextScale = 0.8f;
		private const int Pad = 6;
		private const int BarHeight = 6;
		private const float HungryBelow = 35f; // matches the feed threshold

		public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
		{
			int idx = layers.FindIndex(l => l.Name == "Vanilla: Entity Health Bars");
			// Game scale: world-anchored like vanilla health bars, follows zoom.
			var layer = new LegacyGameInterfaceLayer("FlyRarria: Mote", DrawMote, InterfaceScaleType.Game);
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
			MoteMode? mode = null;
			bool reflex = true;
			// Anchor above the fly; fall back to the player while it's away.
			Vector2 anchor = player.Top;
			if (MoteProjectile.FindFor(player) is MoteProjectile m) {
				mode = m.CurrentMode;
				reflex = m.BrainReflex;
				anchor = m.Projectile.Top;
			}
			var bond = BondSystem.Instance?.Get(player);
			int level = bond?.Level ?? 1;
			float hunger = bond?.Hunger ?? 0f;

			var sb = Main.spriteBatch;
			var font = FontAssets.MouseText.Value;
			string modeText = mode?.ToString() ?? "Away";
			string driveText = reflex ? "reflex" : "brain";
			string levelText = $"Lv{level}";

			float lineH = font.MeasureString("Ay").Y * TextScale;
			float dot = 6f;
			float modeW = dot + 4f + font.MeasureString(modeText).X * TextScale;
			float driveW = font.MeasureString(driveText).X * TextScale;
			float levelW = font.MeasureString(levelText).X * TextScale;
			int width = (int)Math.Max(modeW + 12f + driveW, 110f) + Pad * 2;
			int height = (int)(lineH * 2f) + Pad * 2 - 4;

			Vector2 screen = anchor - Main.screenPosition;
			var panel = new Rectangle((int)(screen.X - width / 2f), (int)(screen.Y - height - 8f), width, height);
			Utils.DrawInvBG(sb, panel, new Color(28, 32, 64) * 0.85f);

			float x = panel.X + Pad;
			float y = panel.Y + Pad - 2;
			float innerW = width - Pad * 2;

			// Row 1: mode (coloured dot + name) on the left, driver on the right.
			Color modeColor = ModeColor(mode);
			Rect(sb, new Vector2(x, y + lineH / 2f - dot / 2f - 1f), new Vector2(dot, dot), modeColor);
			Utils.DrawBorderString(sb, modeText, new Vector2(x + dot + 4f, y), modeColor, TextScale);
			Color driveColor = reflex ? new Color(150, 150, 150) : new Color(120, 230, 255);
			Utils.DrawBorderString(sb, driveText, new Vector2(x + innerW - driveW, y), driveColor, TextScale);

			// Row 2: bond level, then the hunger bar filling the rest.
			y += lineH - 2f;
			Utils.DrawBorderString(sb, levelText, new Vector2(x, y), new Color(255, 215, 120), TextScale);
			float barX = x + levelW + 6f;
			float barW = x + innerW - barX;
			float barY = y + lineH / 2f - BarHeight / 2f - 1f;
			Rect(sb, new Vector2(barX - 1f, barY - 1f), new Vector2(barW + 2f, BarHeight + 2f), Color.Black * 0.7f);
			Rect(sb, new Vector2(barX, barY), new Vector2(barW, BarHeight), new Color(40, 40, 40) * 0.9f);
			Color fill = HungerColor(hunger / 100f);
			if (hunger < HungryBelow) {
				float pulse = 0.65f + 0.35f * (float)Math.Sin(Main.GlobalTimeWrappedHourly * 10f);
				fill *= pulse;
			}
			Rect(sb, new Vector2(barX, barY), new Vector2(barW * hunger / 100f, BarHeight), fill);
			return true;
		}

		private static void Rect(SpriteBatch sb, Vector2 pos, Vector2 size, Color color)
		{
			sb.Draw(TextureAssets.MagicPixel.Value, pos.Floor(), new Rectangle(0, 0, 1, 1), color,
				0f, Vector2.Zero, size, SpriteEffects.None, 0f);
		}

		private static Color HungerColor(float t)
		{
			var red = new Color(230, 70, 60);
			var yellow = new Color(240, 210, 70);
			var green = new Color(110, 220, 90);
			return t < 0.5f ? Color.Lerp(red, yellow, t * 2f) : Color.Lerp(yellow, green, (t - 0.5f) * 2f);
		}

		internal static Color ModeColor(MoteMode? mode) => mode switch {
			MoteMode.Idle => new Color(210, 210, 210),
			MoteMode.Follow => new Color(140, 220, 255),
			MoteMode.Escape => new Color(255, 110, 90),
			MoteMode.Startle => new Color(255, 180, 70),
			MoteMode.Feed => new Color(150, 235, 110),
			MoteMode.Groom => new Color(210, 170, 255),
			MoteMode.Song => new Color(255, 140, 220),
			MoteMode.Sleep => new Color(140, 150, 230),
			_ => new Color(130, 130, 130),
		};
	}
}
