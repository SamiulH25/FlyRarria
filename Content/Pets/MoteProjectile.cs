using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using FlyRarria.Brain;
using FlyRarria.Content.Bond;

namespace FlyRarria.Content.Pets
{
	/// <summary>
	/// The companion. Vanilla pet shell (follow/teleport/keepalive) with the brain
	/// in the decision seat: every 3rd tick the world is sampled into a SensoryFrame,
	/// the LIF circuits step 50ms, and the decoded command steers velocity.
	/// Until circuit data ships (Circuits/*.json), the brain resolves no populations
	/// and the decoder reports Idle/Reflex — the pet then falls back to plain
	/// following so it is still a working pet. No fake brain activity is ever shown.
	/// </summary>
	public class MoteProjectile : ModProjectile
	{
		private const int BrainEveryTicks = 3;
		private const float FollowSpeed = 7f;
		private const float TeleportTiles = 25f;
		private const int MealCooldownBrainTicks = 10 * 60 / BrainEveryTicks; // ~10s; _feedCd counts brain ticks

		private LifNetwork _net;
		private PopulationIndex _pops;
		private MotorDecoder _decoder;
		private MotorCommand _cmd;
		private int _tick;
		private bool _brainLoaded;
		private int _feedCd;
		private float _damageFlash;
		private int _lastLife = -1;

		public MoteMode CurrentMode => _cmd.Mode;
		public bool BrainReflex => _cmd.Reflex;

		public override void SetStaticDefaults()
		{
			Main.projFrames[Type] = 4;
			Main.projPet[Type] = true;
		}

		public override void SetDefaults()
		{
			Projectile.CloneDefaults(ProjectileID.ZephyrFish);
			AIType = ProjectileID.ZephyrFish;
		}

		public override bool PreAI()
		{
			Player player = Main.player[Projectile.owner];
			player.zephyrfish = false;
			return true;
		}

		public override void AI()
		{
			Player player = Main.player[Projectile.owner];
			if (!player.dead && player.HasBuff(ModContent.BuffType<MoteBuff>())) {
				Projectile.timeLeft = 2;
			}

			EnsureBrain();

			if (_tick++ % BrainEveryTicks == 0) {
				var frame = SampleWorld(player);
				var drives = SensoryEncoders.Encode(frame);
				SensoryEncoders.Apply(_net, _pops, drives);
				_net.Step(100); // 100 x 0.5ms = 50ms brain time
				_cmd = _decoder.Decode(brainDriven: _brainLoaded);
				TendBond(player, frame);
			}

			Steer(player);
			Animate();
		}

		private void EnsureBrain()
		{
			if (_net != null) {
				return;
			}
			// Real circuit data when embedded (Circuits/*.json); otherwise an empty
			// graph so the pet stays a working follower and the HUD reports [reflex].
			Connectome graph = CircuitLoader.TryLoadAll();
			_brainLoaded = graph != null;
			graph ??= new Connectome(0, new int[0], new int[0], new ushort[0],
				new sbyte[0], new string[0], new int[0], new double[0], new double[0]);
			_net = new LifNetwork(graph, 0.5, LifNetwork.Params.Shiu2024());
			_pops = new PopulationIndex(graph);
			_decoder = new MotorDecoder(_net, _pops, MotorDecoder.Thresholds.Default);
			_cmd = new MotorCommand { Mode = MoteMode.Follow, Reflex = !_brainLoaded };
		}

		private SensoryFrame SampleWorld(Player player)
		{
			var f = SensoryFrame.Empty;
			Vector2 toPlayer = player.Center - Projectile.Center;
			float dist = toPlayer.Length();

			// Chase: the owner is the target when far, on the side they are on.
			if (dist > 120) {
				float side = MathHelper.Clamp(toPlayer.X / 60f, 0f, 1f);
				if (toPlayer.X < 0) {
					f.ChaseLeft = side;
				}
				else {
					f.ChaseRight = side;
				}
			}

			// Loom: nearest hostile closing in.
			float worst = 0;
			bool left = false;
			for (int i = 0; i < Main.maxNPCs; i++) {
				NPC npc = Main.npc[i];
				if (!npc.active || npc.friendly || npc.lifeMax <= 5 || npc.dontTakeDamage) {
					continue;
				}
				Vector2 d = npc.Center - Projectile.Center;
				if (d.Length() > 480) {
					continue;
				}
				float closing = -Vector2.Dot(npc.velocity, Vector2.Normalize(d));
				float loom = MathHelper.Clamp((closing / 6f) * (1f - d.Length() / 480f), 0f, 1f);
				if (loom > worst) {
					worst = loom;
					left = d.X < 0;
				}
			}
			if (left) {
				f.LoomLeft = worst;
			}
			else {
				f.LoomRight = worst;
			}
			f.WindLeft = MathHelper.Clamp(-Projectile.velocity.X / 12f, 0f, 1f);
			f.WindRight = MathHelper.Clamp(Projectile.velocity.X / 12f, 0f, 1f);
			f.LightLevel = Lighting.Brightness((int)(Projectile.Center.X / 16), (int)(Projectile.Center.Y / 16));
			f.Touch = Main.raining ? 0.4f : 0f;
			ScanFood(player, ref f);
			f.SocialCue = CountCompany(player) > 0 ? 0.6f : 0f;
			f.Heat = player.HasBuff(BuffID.Burning) || player.HasBuff(BuffID.OnFire) ? 1f : 0f;
			ScanSmallObjects(ref f);

			// Owner hurt: a bristle flash that fades over ~0.5s.
			if (_lastLife >= 0 && player.statLife < _lastLife) {
				_damageFlash = 1f;
			}
			else {
				_damageFlash *= 0.75f;
			}
			_lastLife = player.statLife;
			f.DamageFlash = _damageFlash;

			// Interoception: the bond's hunger scales sugar sensing (SensoryEncoders.SugarGain).
			f.Hunger = BondSystem.Instance?.Get(player).Need ?? SensoryFrame.Empty.Hunger;
			return f;
		}

		private void ScanSmallObjects(ref SensoryFrame f)
		{
			// Small moving things (critters, bees, ...) are LC11's preferred stimulus.
			for (int i = 0; i < Main.maxNPCs; i++) {
				NPC npc = Main.npc[i];
				if (!npc.active || npc.width > 24 || npc.height > 24) {
					continue;
				}
				Vector2 d = npc.Center - Projectile.Center;
				float dist = d.Length();
				if (dist > 320) {
					continue;
				}
				float s = MathHelper.Clamp(npc.velocity.Length() / 3f, 0f, 1f) * (1f - dist / 320f);
				if (d.X < 0) {
					f.SmallObjectLeft = MathHelper.Max(f.SmallObjectLeft, s);
				}
				else {
					f.SmallObjectRight = MathHelper.Max(f.SmallObjectRight, s);
				}
			}
		}

		private void ScanFood(Player player, ref SensoryFrame f)
		{
			// Dropped items near the mote: smell in a wide radius, taste on contact.
			for (int i = 0; i < Main.maxItems; i++) {
				Item item = Main.item[i];
				if (!item.active || item.stack <= 0) {
					continue;
				}
				float d = Vector2.Distance(item.Center, Projectile.Center);
				if (IsSweet(item) && d < 16 * 16) {
					f.FoodSmell = MathHelper.Max(f.FoodSmell, 1f - d / (16 * 16));
				}
				if (d < 40) {
					if (IsSweet(item)) {
						f.SugarContact = 1f;
					}
					if (IsBitter(item)) {
						f.BitterContact = 1f;
					}
				}
			}
			// Hand-fed: player holding food close to the mote offers a taste.
			Item held = player.HeldItem;
			if (held != null && held.stack > 0
				&& Vector2.Distance(player.Center, Projectile.Center) < 160) {
				if (IsSweet(held)) {
					f.SugarContact = MathHelper.Max(f.SugarContact, 0.7f);
					f.FoodSmell = MathHelper.Max(f.FoodSmell, 0.5f);
				}
				if (IsBitter(held)) {
					f.BitterContact = 1f;
				}
			}
		}

		private static bool IsSweet(Item item)
		{
			if (item.healLife > 0) {
				return true;
			}
			return item.buffType == BuffID.WellFed
				|| item.buffType == BuffID.WellFed2
				|| item.buffType == BuffID.WellFed3;
		}

		private static bool IsBitter(Item item)
		{
			return item.type == ItemID.RottenChunk
				|| item.type == ItemID.Vertebrae
				|| item.type == ItemID.Stinger
				|| item.type == ItemID.SpiderFang;
		}

		private static int CountCompany(Player player)
		{
			int n = 0;
			for (int i = 0; i < Main.maxPlayers; i++) {
				Player p = Main.player[i];
				if (p.active && !p.dead && i != player.whoAmI
					&& Vector2.Distance(p.Center, player.Center) < 25 * 16) {
					n++;
				}
			}
			return n;
		}

		private void TendBond(Player player, SensoryFrame frame)
		{
			var bond = BondSystem.Instance?.Get(player);
			if (bond == null) {
				return;
			}
			bond.Tick(BrainEveryTicks / 3600f); // game ticks -> real-time minutes
			if (--_feedCd > 0) {
				return;
			}
			if (frame.SugarContact > 0.5f && _cmd.Mode == MoteMode.Feed) {
				bond.Feed(sweet: true);
				_feedCd = MealCooldownBrainTicks;
			}
			else if (frame.BitterContact > 0.5f) {
				bond.Feed(sweet: false);
				_feedCd = MealCooldownBrainTicks;
			}
			else if (_cmd.Mode == MoteMode.Escape) {
				bond.SharedScare();
				_feedCd = MealCooldownBrainTicks;
			}
		}

		private void Steer(Player player)
		{
			Vector2 toPlayer = player.Center - Projectile.Center;
			float dist = toPlayer.Length();

			if (dist > TeleportTiles * 16) {
				Projectile.Center = player.Center + new Vector2(0, -48);
				Projectile.velocity = Vector2.Zero;
				return;
			}
			// Bond-4 nights: perch on the owner's head instead of hovering.
			if (BondSystem.Instance?.IsAsleep(player) == true && dist < 30 * 16) {
				_cmd.Mode = MoteMode.Sleep;
				Vector2 perch = player.Center + new Vector2(0, -46);
				Projectile.velocity = Vector2.Lerp(Projectile.velocity, (perch - Projectile.Center) * 0.2f, 0.2f);
				return;
			}

			Vector2 desired = Vector2.Zero;
			float response = 0.12f;
			switch (_cmd.Mode) {
				case MoteMode.Escape: {
					Vector2 away = Projectile.Center - player.Center;
					if (away == Vector2.Zero) {
						away = -Vector2.UnitY;
					}
					desired = Vector2.Normalize(away + new Vector2(_cmd.Yaw * 40, -60)) * (FollowSpeed * 1.8f);
					break;
				}
				case MoteMode.Startle:
					// Looming seen but no giant-fiber takeoff: freeze in place.
					response = 0.35f;
					break;
				case MoteMode.Feed:
				case MoteMode.Groom:
				case MoteMode.Song:
					desired = toPlayer * 0.02f;
					break;
				default: {
					if (_cmd.Forward < 0 && dist > 1) {
						// MDN backward walking: back away from the owner.
						desired = -Vector2.Normalize(toPlayer) * 3f;
					}
					else if (dist > 96) {
						// DNp09/DNg100 forward drive speeds up the approach.
						desired = Vector2.Normalize(toPlayer) * FollowSpeed * (_cmd.Forward > 0 ? 1.3f : 1f);
					}
					else if (dist < 48) {
						desired = -Vector2.Normalize(toPlayer) * 2f;
					}
					if (_cmd.Yaw != 0) {
						desired += new Vector2(MathHelper.Clamp(_cmd.Yaw / 10f, -2f, 2f), 0);
					}
					break;
				}
			}

			Projectile.velocity = Vector2.Lerp(Projectile.velocity, desired, response);
			if (BondSystem.Instance?.IsAsleep(player) == true) {
				Projectile.velocity *= 0.9f;
			}
		}

		private void Animate()
		{
			Projectile.frameCounter++;
			if (Projectile.frameCounter >= 8) {
				Projectile.frameCounter = 0;
				Projectile.frame = (Projectile.frame + 1) % Main.projFrames[Type];
			}
			Projectile.spriteDirection = Projectile.velocity.X < 0 ? -1 : 1;
			Projectile.rotation = MathHelper.Clamp(Projectile.velocity.X * 0.03f, -0.3f, 0.3f);
		}
	}
}
