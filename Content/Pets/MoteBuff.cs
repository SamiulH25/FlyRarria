using Terraria;
using Terraria.ModLoader;

namespace FlyRarria.Content.Pets
{
	public class MoteBuff : ModBuff
	{
		public override void SetStaticDefaults()
		{
			Main.buffNoTimeDisplay[Type] = true;
			Main.vanityPet[Type] = true;
		}

		public override void Update(Player player, ref int buffIndex)
		{
			bool active = player.ownedProjectileCounts[ModContent.ProjectileType<MoteProjectile>()] > 0;
			if (!active && player.whoAmI == Main.myPlayer) {
				Projectile.NewProjectile(player.GetSource_Buff(buffIndex), player.Center, Microsoft.Xna.Framework.Vector2.Zero,
					ModContent.ProjectileType<MoteProjectile>(), 0, 0f, player.whoAmI);
			}
		}
	}
}
