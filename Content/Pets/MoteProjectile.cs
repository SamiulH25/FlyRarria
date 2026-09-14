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

		private LifNetwork _net;
		private PopulationIndex _pops;
		private MotorDecoder _decoder;
		private MotorCommand _cmd;
		private int _tick;

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
				_cmd = _decoder.Decode(brainDriven: _pops != null && CircuitDataPresent);
			}

			Steer(player);
			Animate();
		}

		private bool CircuitDataPresent => _net != null && _pops != null;

		private void EnsureBrain()
		{
			if (_net != null) {
				return;
			}
			// Empty graph until tools/extract_circuits.py output is embedded.
			// The pet remains a working follower; scope HUD reports [reflex].
			var empty = new Connectome(0, new int[0], new int[0], new ushort[0],
				new sbyte[0], new string[0], new int[0], new double[0], new double[0]);
			_net = new LifNetwork(empty, 0.5, LifNetwork.Params.Shiu2024());
			_pops = new PopulationIndex(empty);
			_decoder = new MotorDecoder(_net, _pops, MotorDecoder.Thresholds.Default);
			_cmd = new MotorCommand { Mode = MoteMode.Follow, Reflex = true };
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
			return f;
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

			Vector2 desired = Vector2.Zero;
			switch (_cmd.Mode) {
				case MoteMode.Escape: {
					Vector2 away = Projectile.Center - player.Center;
					if (away == Vector2.Zero) {
						away = -Vector2.UnitY;
					}
					desired = Vector2.Normalize(away + new Vector2(_cmd.Yaw * 40, -60)) * (FollowSpeed * 1.8f);
					break;
				}
				case MoteMode.Feed:
				case MoteMode.Groom:
				case MoteMode.Song:
					desired = toPlayer * 0.02f;
					break;
				default: {
					if (dist > 96) {
						desired = Vector2.Normalize(toPlayer) * FollowSpeed;
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

			Projectile.velocity = Vector2.Lerp(Projectile.velocity, desired, 0.12f);
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
