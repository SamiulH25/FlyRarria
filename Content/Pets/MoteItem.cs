using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace FlyRarria.Content.Pets
{
	public class MoteItem : ModItem
	{
		public override void SetDefaults()
		{
			Item.CloneDefaults(ItemID.ZephyrFish);
			Item.buffType = ModContent.BuffType<MoteBuff>();
			Item.value = Item.buyPrice(gold: 1);
			Item.rare = ItemRarityID.Blue;
		}

		public override void UseStyle(Player player, Rectangle heldItemFrame)
		{
			if (player.whoAmI == Main.myPlayer && player.itemTime == 0) {
				player.AddBuff(Item.buffType, 3600);
			}
		}

		public override void AddRecipes()
		{
			CreateRecipe()
				.AddIngredient(ItemID.BottledHoney, 5)
				.AddIngredient(ItemID.Blinkroot, 3)
				.AddTile(TileID.Bottles)
				.Register();
		}
	}
}
