namespace FlyRarria.Brain
{
	/// <summary>
	/// What the companion senses this brain tick. Built on the game thread from
	/// Terraria state (positions, tiles, light, weather); consumed by encoders.
	/// All strengths are 0..1. Left/right are the companion's own sides.
	/// </summary>
	public struct SensoryFrame
	{
		public float LoomLeft;
		public float LoomRight;
		public float ChaseLeft;
		public float ChaseRight;
		public float SmallObjectLeft;
		public float SmallObjectRight;
		public float SugarContact;
		public float BitterContact;
	/// <summary>Food-odor strength per side: the brain turns toward the stronger one.</summary>
	public float FoodSmellLeft;
	public float FoodSmellRight;
		public float WindLeft;
		public float WindRight;
		public float Touch;
		public float DamageFlash;
		public float LightLevel;
		public float Heat;
		public float SocialCue;
		/// <summary>Interoceptive hunger drive: 0 full .. 1 starving. Scales sugar sensing.</summary>
		public float Hunger;

		public static SensoryFrame Empty => new SensoryFrame { LightLevel = 0.5f, Hunger = 0.2f };
	}
}
