using System;
using System.Collections.Generic;
using System.Diagnostics;
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
	/// the network steps 50ms on worker threads, and the decoded command steers velocity.
	/// The brain is the whole male CNS when Circuits/male-cns.connectome.gz ships, else the
	/// merged circuit JSON. It loads in the background (about a second for the whole CNS);
	/// until then, or with no circuit data at all, the pet falls back to plain following
	/// and the HUD says so. No fake brain activity is ever shown.
	/// </summary>
	public class MoteProjectile : ModProjectile
	{
		private const int BrainEveryTicks = 3;
		private const float FollowSpeed = 7f;
		private const float TeleportTiles = 25f;
		private const int MealCooldownBrainTicks = 10 * 60 / BrainEveryTicks; // ~10s; _feedCd counts brain ticks

		/// <summary>Threads stepping the brain: most cores, leaving some to the game.</summary>
		private static int BrainShards => Math.Clamp(Environment.ProcessorCount * 3 / 4, 1, 12);

		private sealed class BrainParts
		{
			public Connectome Graph;
			public LifNetwork Net;
			public PopulationIndex Pops;
		}

		private Task<BrainParts> _brainLoad;
		private LifNetwork _net;
		private PopulationIndex _pops;
		private MotorDecoder _decoder;
		private MotorCommand _cmd = new MotorCommand { Mode = MoteMode.Follow, Reflex = true };
		private int _ticksSinceBrainStart = BrainEveryTicks - 1;
		/// <summary>The brain step running on worker threads, or null while the network is idle.</summary>
		private Task _brainStep;
		private SensoryFrame _stepFrame;
		private List<(string type, string side, double hz)> _stepDrives;
		private int _stepGameTicks = BrainEveryTicks;
		private double _stepMs; // written by the step's worker, read once it's done
		private bool _brainLoaded;
		private int _feedCd;
		private int _scareCd;
		private float _damageFlash;
		private int _lastLife = -1;
	/// <summary>Offset to the worst closing hostile at the last sample, so Escape flees the threat.</summary>
	private Vector2 _threatOffset;
	private bool _hasThreat;
	/// <summary>Strongest food smell's position at the last sample, so SEEK has somewhere to go.</summary>
	private Vector2 _foodPos;
	private bool _hasFood;
	/// <summary>Dropped sweet the meal was tasted from; eaten when the meal counts.</summary>
	private int _mealItem = -1;
	private float _mealDist;
	/// <summary>Nearest pickup worth fetching, for idle minds.</summary>
	private Vector2 _fetchPos;
	private int _fetchItem = -1;
	private bool _hasFetch;

		public MoteMode CurrentMode => _cmd.Mode;
		public bool BrainReflex => _cmd.Reflex;
		/// <summary>True until the background brain load finishes.</summary>
		public bool BrainLoading => _net == null;
		/// <summary>The loaded circuit graph, or null while loading or running the [reflex] fallback.</summary>
		public Connectome Graph { get; private set; }
		/// <summary>
		/// The live network. It steps on worker threads, so read <see cref="LastSpikes"/> rather
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
	/// <summary>The world sample driving the latest brain step (for debug readouts).</summary>
	public SensoryFrame LastSample => _stepFrame;
		/// <summary>Wall-clock time of the latest 50 ms brain step.</summary>
		public double LastStepMs { get; private set; }
		/// <summary>Brain time over game time, smoothed: below 1 when steps overrun their 3 ticks.</summary>
		public double RealTimeFactor { get; private set; } = 1;

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

			// A busy whole-brain step mustn't stall the frame, so it runs on workers and its
			// result lands the first frame after it finishes. A step that overruns its 3 ticks
			// delays the next one: the brain runs slower than real time instead of the game.
			if (_net != null) {
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
					_brainStep = Task.Run(() => {
						var clock = Stopwatch.StartNew();
						net.Step(100); // 100 x 0.5ms = 50ms brain time
						_stepMs = clock.Elapsed.TotalMilliseconds;
					});
				}
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
			LastStepMs = _stepMs;
			RealTimeFactor += 0.2 * ((double)BrainEveryTicks / Math.Max(BrainEveryTicks, _stepGameTicks) - RealTimeFactor);
			BrainTicks++;
			_cmd = _decoder.Decode(brainDriven: _brainLoaded);
			TendBond(player, _stepFrame, _stepGameTicks);
		}

		private void EnsureBrain()
		{
			if (_net != null) {
				return;
			}
			_brainLoad ??= Task.Run(() => BuildBrain(CircuitLoader.LoadShared(out var parameters), parameters));
			if (!_brainLoad.IsCompleted) {
				return; // following on reflex meanwhile
			}
			BrainParts parts;
			try {
				parts = _brainLoad.GetAwaiter().GetResult();
			}
			catch (Exception e) {
				Mod.Logger.Error("brain failed to load, running [reflex]", e);
				parts = BuildBrain(null, LifNetwork.Params.Shiu2024());
			}
			_brainLoaded = parts.Graph != null;
			Graph = parts.Graph;
			_net = parts.Net;
			_pops = parts.Pops;
			_decoder = new MotorDecoder(_net, _pops, MotorDecoder.Thresholds.Default);
			_cmd = new MotorCommand { Mode = MoteMode.Follow, Reflex = !_brainLoaded };
		}

		/// <summary>
		/// A network over <paramref name="graph"/>, or over an empty graph when it's null so the pet
		/// stays a working follower and the HUD reports [reflex].
		/// </summary>
		private static BrainParts BuildBrain(Connectome graph, LifNetwork.Params parameters)
		{
			Connectome g = graph ?? new Connectome(0, new int[0], new int[0], new ushort[0],
				new sbyte[0], new string[0], new int[0], new double[0], new double[0]);
			var parts = new BrainParts {
				Graph = graph,
				Net = new LifNetwork(g, 0.5, parameters, shards: BrainShards),
				Pops = new PopulationIndex(g),
			};
			// Resolve every population the encoders, decoder and neuroscope use now, off the game
			// thread: each new lookup scans every neuron's type, and later ones are cache hits.
			foreach (var (type, side, _) in SensoryEncoders.Encode(SensoryEncoders.EveryChannel)) {
				parts.Pops.Resolve(type, side);
			}
			foreach (var (type, side) in MotorDecoder.Readouts) {
				parts.Pops.Resolve(type, side);
			}
			parts.Pops.Resolve("LC4"); // STARTLE reads the loom detectors on both sides
			parts.Pops.Resolve("LPLC2");
			return parts;
		}
	/// <summary>Worst closing hostile right now: its offset from the mote and loom strength.</summary>
	private bool TryFindThreat(out Vector2 offset, out float loom)
	{
		offset = Vector2.Zero;
		loom = 0;
		bool found = false;
		for (int i = 0; i < Main.maxNPCs; i++) {
			NPC npc = Main.npc[i];
			if (!npc.active || npc.friendly || npc.lifeMax <= 5 || npc.dontTakeDamage) {
				continue;
			}
			Vector2 d = npc.Center - Projectile.Center;
			if (!float.IsFinite(d.X) || !float.IsFinite(d.Y) || d.Length() > 480) {
				continue;
			}
			float closing = d == Vector2.Zero ? 0 : -Vector2.Dot(npc.velocity, Vector2.Normalize(d));
			float l = MathHelper.Clamp((closing / 6f) * (1f - d.Length() / 480f), 0f, 1f);
			if (l > loom) {
				loom = l;
				offset = d;
				found = true;
			}
		}
		return found;
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

		// Loom: worst closing hostile, on the side it's on. Its offset is kept
		// so Escape flees the threat instead of the owner.
		if (TryFindThreat(out Vector2 threat, out float loom)) {
			_threatOffset = threat;
			_hasThreat = true;
			if (threat.X < 0) {
				f.LoomLeft = loom;
			}
			else {
				f.LoomRight = loom;
			}
		}
		else {
			_hasThreat = false;
		}
			// Wind: world wind only, and only outdoors. Airflow from the mote's own flight
			// is left out (flies cancel self-motion with efference copy); feeding it in
			// drove JO past the groom threshold at follow speed. On the whole CNS, JO input
			// 0.045-0.09 grooms in bursts and 0.1+ grooms steadily. Terraria's weather wind
			// tops out near 0.8 (windy days from 0.34-0.4), so the curve stays under that band
			// up to 0.6 and crosses it by ~0.66: grooming is for strong wind only.
			if (Projectile.Center.Y / 16f < Main.worldSurface) {
				float speed = Math.Abs(Main.windSpeedCurrent);
				float jo = MathHelper.Clamp(speed <= 0.6f ? speed * 0.06f : 0.036f + (speed - 0.6f), 0f, 1f);
				bool fromLeft = Main.windSpeedCurrent > 0; // blowing rightward arrives from the left
				f.WindLeft = fromLeft ? jo : 0f;
				f.WindRight = fromLeft ? 0f : jo;
			}
			f.LightLevel = Lighting.Brightness((int)(Projectile.Center.X / 16), (int)(Projectile.Center.Y / 16));
			f.Touch = Main.raining ? 0.4f : 0f;
			ScanFood(player, ref f);
			f.SocialCue = CountCompany(player) > 0 ? 0.6f : 0f;
			f.Heat = player.HasBuff(BuffID.Burning) || player.HasBuff(BuffID.OnFire) ? 1f : 0f;
			ScanSmallObjects(ref f);
		ScanPickups(player);

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

	private void ScanPickups(Player player)
	{
		// Hearts, stars and coins near the mote but out of the owner's reach are
		// worth fetching. Runs on the sample tick; Steer navigates to _fetchPos.
		_hasFetch = false;
		float best = 200;
		for (int i = 0; i < Main.maxItems; i++) {
			Item item = Main.item[i];
			if (!item.active || item.stack <= 0 || !IsPickup(item)) {
				continue;
			}
			if (Vector2.Distance(item.Center, player.Center) < 48) {
				continue; // the owner grabs those anyway
			}
			float dm = Vector2.Distance(item.Center, Projectile.Center);
			if (dm < best) {
				best = dm;
				_fetchPos = item.Center;
				_fetchItem = i;
				_hasFetch = true;
			}
		}
	}

	private static bool IsPickup(Item item) => item.type == ItemID.Heart || item.type == ItemID.Star
		|| item.type == ItemID.CopperCoin || item.type == ItemID.SilverCoin
		|| item.type == ItemID.GoldCoin || item.type == ItemID.PlatinumCoin;

	private void ScanFood(Player player, ref SensoryFrame f)
	{
		// Dropped items near the mote: smell in a wide radius (split by side so
		// the brain can turn toward it), taste on contact. Tracks the strongest
		// smell's position so SEEK has somewhere to go.
		_hasFood = false;
		_mealItem = -1;
		float best = 0;
		for (int i = 0; i < Main.maxItems; i++) {
			Item item = Main.item[i];
			if (!item.active || item.stack <= 0) {
				continue;
			}
			float d = Vector2.Distance(item.Center, Projectile.Center);
			if (IsSweet(item) && d < 16 * 16) {
				float s = 1f - d / (16 * 16);
				if (item.Center.X < Projectile.Center.X) {
					f.FoodSmellLeft = MathHelper.Max(f.FoodSmellLeft, s);
				}
				else {
					f.FoodSmellRight = MathHelper.Max(f.FoodSmellRight, s);
				}
				if (s > best) {
					best = s;
					_foodPos = item.Center;
					_hasFood = true;
				}
			}
		if (d < 40) {
			if (IsSweet(item)) {
				f.SugarContact = 1f;
				if (_mealItem < 0 || d < _mealDist) {
					_mealItem = i;
					_mealDist = d;
				}
			}
				if (IsBitter(item)) {
					f.BitterContact = 1f;
				}
			}
		}
		// Hand-fed: player holding food close to the mote offers a taste. Its
		// smell sits on the owner's side, and the owner is where to go.
		Item held = player.HeldItem;
		if (held != null && held.stack > 0
			&& Vector2.Distance(player.Center, Projectile.Center) < 160) {
			if (IsSweet(held)) {
				f.SugarContact = MathHelper.Max(f.SugarContact, 0.7f);
				if (player.Center.X < Projectile.Center.X) {
					f.FoodSmellLeft = MathHelper.Max(f.FoodSmellLeft, 0.5f);
				}
				else {
					f.FoodSmellRight = MathHelper.Max(f.FoodSmellRight, 0.5f);
				}
				if (0.5f > best) {
					_foodPos = player.Center;
					_hasFood = true;
				}
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
		// Scares bond on their own cooldown: fleeing right after a meal still counts.
		if (--_scareCd <= 0 && _cmd.Mode == MoteMode.Escape) {
			bond.SharedScare();
			_scareCd = MealCooldownBrainTicks;
		}
		if (--_feedCd > 0) {
			return;
		}
		if (frame.SugarContact > 0.5f && _cmd.Mode == MoteMode.Feed) {
			bond.Feed(sweet: true);
			_feedCd = MealCooldownBrainTicks;
			EatMealItem(player);
		}
		else if (frame.BitterContact > 0.5f) {
			bond.Feed(sweet: false);
			_feedCd = MealCooldownBrainTicks;
		}
	}

	/// <summary>Eats the sweet item the meal was tasted from (dropped only; hand-fed
	/// is a free nibble). Owner-client only, synced, revalidated: the brain tick lags.</summary>
	private void EatMealItem(Player player)
	{
		if (Projectile.owner != Main.myPlayer || _mealItem < 0 || _mealItem >= Main.maxItems) {
			return;
		}
		Item item = Main.item[_mealItem];
		if (!item.active || item.stack <= 0 || !IsSweet(item)
			|| Vector2.Distance(item.Center, Projectile.Center) > 60) {
			return;
		}
		if (--item.stack <= 0) {
			item.TurnToAir();
		}
		if (Main.netMode != NetmodeID.SinglePlayer) {
			NetMessage.SendData(MessageID.SyncItem, -1, -1, null, _mealItem);
		}
		_mealItem = -1;
	}

	/// <summary>Hands the fetched pickup to the owner by moving it onto them; vanilla
	/// pickup does the rest. Owner-client only and synced.</summary>
	private void GiveFetchTo(Player player)
	{
		if (Projectile.owner != Main.myPlayer || !_hasFetch) {
			return;
		}
		if (_fetchItem < 0 || _fetchItem >= Main.maxItems) {
			return;
		}
		Item item = Main.item[_fetchItem];
		if (!item.active || item.stack <= 0 || !IsPickup(item)) {
			return;
		}
		item.Center = player.Center;
		item.velocity = Vector2.Zero;
		item.noGrabDelay = Math.Min(item.noGrabDelay, 30);
		if (Main.netMode != NetmodeID.SinglePlayer) {
			NetMessage.SendData(MessageID.SyncItem, -1, -1, null, _fetchItem);
		}
		_hasFetch = false;
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
		// Fetch: idle minds pick up after the owner. Game-side chore like the
		// Sleep perch — the brain never sees pickups, it just has nothing on.
		bool fetching = _hasFetch && (_cmd.Mode == MoteMode.Idle || _cmd.Mode == MoteMode.Follow);
		if (fetching) {
			Vector2 toFetch = _fetchPos - Projectile.Center;
			if (toFetch.Length() < 28) {
				GiveFetchTo(player);
			}
			else {
				desired = toFetch.SafeNormalize(Vector2.UnitY) * FollowSpeed;
			}
		}
		else switch (_cmd.Mode) {
			case MoteMode.Escape: {
				// Flee the actual threat, not the owner: the brain fires Escape on
				// any giant-fiber burst, and the bearing comes from the latest sample.
				Vector2 away = _hasThreat && _threatOffset != Vector2.Zero
					? -_threatOffset
					: Projectile.Center - player.Center;
				if (away == Vector2.Zero) {
					away = -Vector2.UnitY;
				}
				desired = Vector2.Normalize(away + new Vector2(_cmd.Yaw * 40, -60)) * (FollowSpeed * 1.8f);
				break;
			}
			case MoteMode.Seek: {
				// Hungry and smells food: close on the strongest smell's sampled
				// position. Contact flips it to FEED (higher in the ladder), which
				// is what actually counts the meal.
				if (_hasFood) {
					Vector2 toFood = _foodPos - Projectile.Center;
					desired = toFood.SafeNormalize(Vector2.UnitY) * FollowSpeed;
				}
				else {
					desired = toPlayer * 0.02f;
				}
				break;
			}
				case MoteMode.Startle:
					// Looming seen but no giant-fiber takeoff: freeze in place.
					response = 0.35f;
					break;
			case MoteMode.Feed: {
				// Eating at contact: hold position with a nibbling bob.
				float bob = (float)Math.Sin(Main.GameUpdateCount * 0.3) * 0.5f;
				desired = toPlayer * 0.01f + new Vector2(0, bob);
				break;
			}
			case MoteMode.Groom: {
				// Shaking water off: rapid side-to-side shimmy in place.
				float shimmy = (Projectile.frameCounter % 16 < 8) ? 1.5f : -1.5f;
				desired = new Vector2(shimmy, 0);
				break;
			}
		case MoteMode.Song: {
			// Courtship dance: slow orbit around the nearest company, else the owner.
			Vector2 stage = player.Center;
			float nearest = 25 * 16;
			for (int i = 0; i < Main.maxPlayers; i++) {
				Player p = Main.player[i];
				if (!p.active || p.dead || i == player.whoAmI) {
					continue;
				}
				float d = Vector2.Distance(p.Center, Projectile.Center);
				if (d < nearest) {
					nearest = d;
					stage = p.Center;
				}
			}
			Vector2 toStage = stage - Projectile.Center;
			Vector2 dir = toStage.SafeNormalize(Vector2.UnitY);
			desired = new Vector2(-dir.Y, dir.X) * 2f + toStage * 0.01f;
			break;
		}
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
		if (!Main.dedServ) {
			if (_cmd.Mode == MoteMode.Escape && Main.rand.NextBool(6)) {
				Dust.NewDust(Projectile.position, Projectile.width, Projectile.height, DustID.Smoke);
			}
			else if (_cmd.Mode == MoteMode.Groom && Main.rand.NextBool(24)) {
				Dust.NewDust(Projectile.position, Projectile.width, Projectile.height, DustID.Water);
			}
		}
		}
	}
}
