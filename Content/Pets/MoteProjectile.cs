using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
	/// the LIF circuits step 50ms on a worker thread, and the decoded command steers velocity.
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
		private int _ticksSinceBrainStart = BrainEveryTicks - 1;
		/// <summary>The brain step running on a worker thread, or null while the network is idle.</summary>
		private Task _brainStep;
		private SensoryFrame _stepFrame;
		private List<(string type, string side, double hz)> _stepDrives;
		private int _stepGameTicks;
		private bool _brainLoaded;
		private int _feedCd;
		private float _damageFlash;
		private int _lastLife = -1;

		public MoteMode CurrentMode => _cmd.Mode;
		public bool BrainReflex => _cmd.Reflex;
		/// <summary>The loaded circuit graph, or null while running the [reflex] fallback.</summary>
		public Connectome Graph { get; private set; }
		/// <summary>
		/// The live network. It steps on a worker thread, so read <see cref="LastSpikes"/> rather
		/// than its spike list; its rates are only written at the end of a step and are fine to display.
		/// </summary>
		public LifNetwork Net => _net;
		public PopulationIndex Pops => _pops;
		/// <summary>Brain steps finished so far; LastSpikes belongs to the latest one.</summary>
		public int BrainTicks { get; private set; }
		/// <summary>Every spike of the latest finished brain step, copied so the next step can run.</summary>
		public int[] LastSpikes { get; private set; } = new int[0];
		/// <summary>The sensory drives applied on the latest brain step.</summary>
		public IReadOnlyList<(string type, string side, double hz)> LastDrives { get; private set; } = new List<(string, string, double)>();

		/// <summary>The player's active mote, or null.</summary>
		public static MoteProjectile FindFor(Player player)
		{
			for (int i = 0; i < Main.maxProjectiles; i++) {
				var p = Main.projectile[i];
				if (p.active && p.type == ModContent.ProjectileType<MoteProjectile>()
					&& p.owner == player.whoAmI && p.ModProjectile is MoteProjectile m) {
					return m;
				}
			}
			return null;
		}

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

			// A busy whole-brain step mustn't stall the frame, so it runs on a worker and its
			// result lands the first frame after it finishes. A step that overruns its 3 ticks
			// delays the next one: the brain runs slower than real time instead of the game.
			if (_brainStep?.IsCompleted == true) {
				FinishBrainStep(player);
			}
			if (++_ticksSinceBrainStart >= BrainEveryTicks && _brainStep == null) {
				_stepFrame = SampleWorld(player);
				_stepDrives = SensoryEncoders.Encode(_stepFrame);
				SensoryEncoders.Apply(_net, _pops, _stepDrives);
				_stepGameTicks = _ticksSinceBrainStart;
				_ticksSinceBrainStart = 0;
				LifNetwork net = _net;
				_brainStep = Task.Run(() => net.Step(100)); // 100 x 0.5ms = 50ms brain time
			}

			Steer(player);
			Animate();
		}

		private void FinishBrainStep(Player player)
		{
			Task step = _brainStep;
			_brainStep = null;
			step.GetAwaiter().GetResult(); // a failed step throws here, on the game thread
			LastSpikes = _net.SpikesThisTick.ToArray();
			LastDrives = _stepDrives;
			BrainTicks++;
			_cmd = _decoder.Decode(brainDriven: _brainLoaded);
			TendBond(player, _stepFrame, _stepGameTicks);
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
			Graph = graph;
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

			// Chase: the owner is LC10a's target when far or on the move, on the side
			// they are on. Close and still reads as nothing to chase, so the brain rests.
			if (dist > 120 || player.velocity.Length() > 1f) {
				float side = MathHelper.Clamp(System.Math.Abs(toPlayer.X) / 60f, 0.3f, 1f);
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
			// Wind: world wind only, and only outdoors. Airflow from the mote's own flight
			// is left out (flies cancel self-motion with efference copy); feeding it in
			// drove JO past the groom threshold at follow speed. JO tips into GROOM between
			// 0.06 and 0.08 regardless of chase, so x0.1 grooms only in strong wind (~0.7+).
			if (Projectile.Center.Y / 16f < Main.worldSurface) {
				float wind = MathHelper.Clamp(Main.windSpeedCurrent * 0.1f, -1f, 1f);
				f.WindLeft = MathHelper.Max(wind, 0f); // blowing rightward arrives from the left
				f.WindRight = MathHelper.Max(-wind, 0f);
			}
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

		private void TendBond(Player player, SensoryFrame frame, int gameTicks)
		{
			var bond = BondSystem.Instance?.Get(player);
			if (bond == null) {
				return;
			}
			bond.Tick(gameTicks / 3600f); // game ticks -> real-time minutes
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

			// A NaN position fails every distance test, so it also has to trigger the teleport,
			// or the mote (and the HUD anchored to it) vanishes for good.
			bool lost = !float.IsFinite(dist) || !float.IsFinite(Projectile.velocity.X) || !float.IsFinite(Projectile.velocity.Y);
			if (lost || dist > TeleportTiles * 16) {
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
						// The buff spawns the mote exactly on the owner (dist 0): Normalize would give NaN.
						desired = -toPlayer.SafeNormalize(Vector2.UnitY) * 2f;
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
