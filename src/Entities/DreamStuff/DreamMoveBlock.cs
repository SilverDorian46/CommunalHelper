﻿using Celeste.Mod.Entities;
using FMOD.Studio;
using Microsoft.Xna.Framework;
using Monocle;
using MonoMod.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace Celeste.Mod.CommunalHelper.Entities {
    [CustomEntity("CommunalHelper/DreamMoveBlock")]
    [TrackedAs(typeof(DreamBlock))]
    public class DreamMoveBlock : CustomDreamBlock {

        private static MethodInfo m_Pooler_Create = typeof(Pooler).GetMethod("Create").MakeGenericMethod(typeof(MoveBlock).GetNestedType("Debris", BindingFlags.NonPublic));

        private enum MovementState {
            Idling,
            Moving,
            Breaking
        }

        public static ParticleType P_Activate;
        public static ParticleType P_Break;
        public static ParticleType[] dreamParticles;
        private int moveParticleIndex = 0;
        private int breakParticleIndex = 0;
        private int activateParticleIndex = 0;

        private const float Accel = 300f;
        private const float MoveSpeed = 60f;
        private const float FastMoveSpeed = 75f;

        private const float SteerSpeed = (float) Math.PI * 16f;
        private const float MaxAngle = (float) Math.PI / 4f;
        private const float NoSteerTime = 0.2f;

        private const float CrashTime = 0.15f;
        private const float CrashResetTime = 0.1f;
        private const float RegenTime = 3f;

        private bool fast;

        private MoveBlock.Directions direction;
        private float homeAngle;
        private Vector2 startPosition;
        private MovementState state = MovementState.Idling;

        private float speed;
        private float targetSpeed;

        private float angle;
        private float targetAngle;

        private Player noSquish;

        private List<MTexture> arrows = new List<MTexture>();

        private float flash;
        private SoundSource moveSfx;

        private bool triggered;
        private float particleRemainder;

        private Coroutine controller;
        private bool noCollide;

        static DreamMoveBlock() {
            P_Activate = new ParticleType(MoveBlock.P_Activate);
            P_Activate.Color = Color.White;
            P_Break = new ParticleType(MoveBlock.P_Break);
            P_Break.Color = Color.White;

            dreamParticles = new ParticleType[4];
            ParticleType particle = new ParticleType(MoveBlock.P_Move);
            particle.ColorMode = ParticleType.ColorModes.Choose;
            particle.Color = Calc.HexToColor("FFEF11");
            particle.Color2 = Calc.HexToColor("FF00D0");
            dreamParticles[0] = particle;

            particle = new ParticleType(particle);
            particle.Color = Calc.HexToColor("08a310");
            particle.Color2 = Calc.HexToColor("5fcde4");
            dreamParticles[1] = particle;

            particle = new ParticleType(particle);
            particle.Color = Calc.HexToColor("7fb25e");
            particle.Color2 = Calc.HexToColor("E0564C");
            dreamParticles[2] = particle;

            particle = new ParticleType(particle);
            particle.Color = Calc.HexToColor("5b6ee1");
            particle.Color2 = Calc.HexToColor("CC3B3B");
            dreamParticles[3] = particle;
        }

        public DreamMoveBlock(EntityData data, Vector2 offset)
            : base(data.Position + offset, data.Width, data.Height, data.Bool("featherMode"), data.Bool("oneUse"), data.Bool("doubleRefill", false)) {
            startPosition = Position;

            fast = data.Bool("fast");
            noCollide = data.Bool("noCollide");

            direction = data.Enum<MoveBlock.Directions>("direction");
            switch (direction) {
                default:
                    homeAngle = targetAngle = angle = 0f;
                    break;
                case MoveBlock.Directions.Left:
                    homeAngle = targetAngle = angle = (float) Math.PI;
                    break;
                case MoveBlock.Directions.Up:
                    homeAngle = targetAngle = angle = -(float) Math.PI / 2f;
                    break;
                case MoveBlock.Directions.Down:
                    homeAngle = targetAngle = angle = (float) Math.PI / 2f;
                    break;
            }

            arrows = GFX.Game.GetAtlasSubtextures("objects/CommunalHelper/dreamMoveBlock/arrow");
            Add(moveSfx = new SoundSource());
            Add(controller = new Coroutine(Controller()));
            Add(new LightOcclude(0.5f));
        }

        private IEnumerator Controller() {
            while (true) {
                triggered = false;
                state = MovementState.Idling;
                while (!triggered && !HasPlayerRider()) {
                    yield return null;
                }


                Audio.Play(CustomSFX.game_dreamMoveBlock_dream_move_block_activate, Position);
                state = MovementState.Moving;
                StartShaking(0.2f);
                ActivateParticles();
                yield return 0.2f;


                targetSpeed = fast ? FastMoveSpeed : MoveSpeed;
                moveSfx.Play(SFX.game_04_arrowblock_move_loop);
                moveSfx.Param("arrow_stop", 0f);
                StopPlayerRunIntoAnimation = false;
                float crashTimer = CrashTime;
                float crashResetTimer = CrashResetTime;
                while (true) {
                    if (Scene.OnInterval(0.02f) && Collidable) {
                        MoveParticles();
                    }
                    speed = Calc.Approach(speed, targetSpeed, Accel * Engine.DeltaTime);
                    angle = Calc.Approach(angle, targetAngle, SteerSpeed * Engine.DeltaTime);
                    Vector2 move = Calc.AngleToVector(angle, speed) * Engine.DeltaTime;
                    bool hit;
                    if (direction == MoveBlock.Directions.Right || direction == MoveBlock.Directions.Left) {
                        hit = MoveCheck(move.XComp());
                        noSquish = Scene.Tracker.GetEntity<Player>();
                        MoveVCollideSolids(move.Y, thruDashBlocks: false);
                        noSquish = null;
                        if (Scene.OnInterval(0.03f)) {
                            if (move.Y > 0f) {
                                ScrapeParticles(Vector2.UnitY);
                            } else if (move.Y < 0f) {
                                ScrapeParticles(-Vector2.UnitY);
                            }
                        }
                    } else {
                        hit = MoveCheck(move.YComp());
                        noSquish = Scene.Tracker.GetEntity<Player>();
                        MoveHCollideSolids(move.X, thruDashBlocks: false);
                        noSquish = null;
                        if (Scene.OnInterval(0.03f)) {
                            if (move.X > 0f) {
                                ScrapeParticles(Vector2.UnitX);
                            } else if (move.X < 0f) {
                                ScrapeParticles(-Vector2.UnitX);
                            }
                        }
                        if (direction == MoveBlock.Directions.Down && Top > SceneAs<Level>().Bounds.Bottom + 32) {
                            hit = true;
                        }
                    }
                    if (hit) {
                        moveSfx.Param("arrow_stop", 1f);
                        crashResetTimer = CrashResetTime;
                        if (!(crashTimer > 0f) && !shattering) {
                            break;
                        }
                        crashTimer -= Engine.DeltaTime;
                    } else {
                        moveSfx.Param("arrow_stop", 0f);
                        if (crashResetTimer > 0f) {
                            crashResetTimer -= Engine.DeltaTime;
                        } else {
                            crashTimer = CrashTime;
                        }
                    }
                    Level level = Scene as Level;
                    if (Left < level.Bounds.Left || Top < level.Bounds.Top || Right > level.Bounds.Right) {
                        break;
                    }
                    yield return null;
                }


                Audio.Play(CustomSFX.game_dreamMoveBlock_dream_move_block_break, Position);
                moveSfx.Stop();
                state = MovementState.Breaking;
                speed = targetSpeed = 0f;
                angle = targetAngle = homeAngle;
                StartShaking(0.2f);
                StopPlayerRunIntoAnimation = true;
                yield return 0.2f;


                BreakParticles();
                List<DynamicData> debris = new List<DynamicData>();
                for (int x = 0; x < Width; x += 8) {
                    for (int y = 0; y < Height; y += 8) {
                        Vector2 offset = new Vector2(x + 4f, y + 4f);
                        DynamicData d = new DynamicData(m_Pooler_Create.Invoke(Engine.Pooler, null));
                        d.Get<Image>("sprite").Texture = Calc.Random.Choose(GFX.Game.GetAtlasSubtextures("objects/CommunalHelper/dreamMoveBlock/debris"));
                        d.Get<Image>("sprite").Color = baseData.Get<Color>("activeLineColor");
                        d.Invoke("Init", Position + offset, Center, startPosition + offset);
                        debris.Add(d);
                        Scene.Add((Entity) d.Target);
                    }
                }
                MoveStaticMovers(startPosition - Position);
                DisableStaticMovers();
                Position = startPosition;
                Visible = Collidable = false;
                yield return 2.2f;


                foreach (DynamicData d in debris) {
                    d.Invoke("StopMoving");
                }
                while (CollideCheck<Actor>() || (noCollide ? CollideCheck<DreamBlock>() : CollideCheck<Solid>())) {
                    yield return null;
                }


                Collidable = true;
                EventInstance sound = Audio.Play(SFX.game_04_arrowblock_reform_begin, ((Entity) debris[0].Target).Position);
                Coroutine component;
                Coroutine routine = component = new Coroutine(SoundFollowsDebrisCenter(sound, debris));
                Add(component);
                foreach (DynamicData d in debris) {
                    d.Invoke("StartShaking");
                }
                yield return 0.2f;


                foreach (DynamicData d in debris) {
                    d.Invoke("ReturnHome", 0.65f);
                }
                yield return 0.6f;


                routine.RemoveSelf();
                foreach (DynamicData d in debris) {
                    d.Invoke("RemoveSelf");
                }
                Audio.Play(CustomSFX.game_dreamMoveBlock_dream_move_block_reappear, Position);
                Visible = true;
                EnableStaticMovers();
                speed = targetSpeed = 0f;
                angle = targetAngle = homeAngle;
                noSquish = null;
                flash = 1f;
            }
        }

        protected override void OneUseDestroy() {
            base.OneUseDestroy();
            Remove(controller);
            moveSfx.Stop();
        }

        protected override bool ShatterCheck() {
            return base.ShatterCheck() && state != MovementState.Breaking;
        }

        private IEnumerator SoundFollowsDebrisCenter(EventInstance instance, List<DynamicData> debris) {
            while (true) {
                instance.getPlaybackState(out PLAYBACK_STATE state);
                if (state == PLAYBACK_STATE.STOPPED) {
                    break;
                }
                Vector2 center = Vector2.Zero;
                foreach (DynamicData d in debris) {
                    center += d.Get<Vector2>("Position");
                }
                center /= debris.Count;
                Audio.Position(instance, center);
                yield return null;
            }
        }

        public override void Update() {
            base.Update();
            if (moveSfx != null && moveSfx.Playing) {
                float num = (Calc.AngleToVector(angle, 1f) * new Vector2(-1f, 1f)).Angle();
                int num2 = (int) Math.Floor((0f - num + (float) Math.PI * 2f) % ((float) Math.PI * 2f) / ((float) Math.PI * 2f) * 8f + 0.5f);
                moveSfx.Param("arrow_influence", num2 + 1);
            }
            flash = Calc.Approach(flash, 0f, Engine.DeltaTime * 5f);
        }

        public override void OnStaticMoverTrigger(StaticMover sm) {
            triggered = true;
        }

        public override void MoveHExact(int move) {
            if (noSquish != null && ((move < 0 && noSquish.X < X) || (move > 0 && noSquish.X > X))) {
                while (move != 0 && noSquish.CollideCheck<Solid>(noSquish.Position + Vector2.UnitX * move)) {
                    move -= Math.Sign(move);
                }
            }
            base.MoveHExact(move);
        }

        public override void MoveVExact(int move) {
            if (noSquish != null && move < 0 && noSquish.Y <= Y) {
                while (move != 0 && noSquish.CollideCheck<Solid>(noSquish.Position + Vector2.UnitY * move)) {
                    move -= Math.Sign(move);
                }
            }
            base.MoveVExact(move);
        }

        private bool MoveCheck(Vector2 speed) {
            if (speed.X != 0f) {
                if (!noCollide || CollideCheck<DreamBlock>(Position + speed.XComp())) {
                    if (MoveHCollideSolids(speed.X, thruDashBlocks: false)) {
                        for (int i = 1; i <= 3; i++) {
                            for (int num = 1; num >= -1; num -= 2) {
                                Vector2 value = new Vector2(Math.Sign(speed.X), i * num);
                                if (!CollideCheck<Solid>(Position + value)) {
                                    MoveVExact(i * num);
                                    MoveHExact(Math.Sign(speed.X));
                                    return false;
                                }
                            }
                        }
                        return true;
                    }
                } else {
                    MoveH(speed.X);
                    return false;
                }
            }
            if (speed.Y != 0f) {
                if (!noCollide || CollideCheck<DreamBlock>(Position + speed.YComp())) {
                    if (MoveVCollideSolids(speed.Y, thruDashBlocks: false)) {
                        for (int j = 1; j <= 3; j++) {
                            for (int num2 = 1; num2 >= -1; num2 -= 2) {
                                Vector2 value2 = new Vector2(j * num2, Math.Sign(speed.Y));
                                if (!CollideCheck<Solid>(Position + value2)) {
                                    MoveHExact(j * num2);
                                    MoveVExact(Math.Sign(speed.Y));
                                    return false;
                                }
                            }
                        }
                        return true;
                    }
                } else {
                    MoveV(speed.Y);
                    return false;
                }
            }
            return false;
        }

        public override void Render() {
            Vector2 position = Position;
            Position += Shake;
            base.Render();

            Color color = Color.Lerp(baseData.Get<Color>("activeLineColor"), Color.Black, ColorLerp);
            if (state != MovementState.Breaking) {
                int value = (int) Math.Floor((0f - angle + (float) Math.PI * 2f) % ((float) Math.PI * 2f) / ((float) Math.PI * 2f) * 8f + 0.5f);
                MTexture arrow = arrows[Calc.Clamp(value, 0, 7)];
                arrow.DrawCentered(Center + baseData.Get<Vector2>("shake"), color);
            } else {
                GFX.Game["objects/CommunalHelper/dreamMoveBlock/x"].DrawCentered(Center + baseData.Get<Vector2>("shake"), color);
            }
            float num = flash * 4f;
            Draw.Rect(X - num, Y - num, Width + num * 2f, Height + num * 2f, Color.White * flash);
            Position = position;
        }

        private void ActivateParticles() {
            bool flag = direction == MoveBlock.Directions.Down || direction == MoveBlock.Directions.Up;
            bool flag2 = !CollideCheck<Player>(Position - Vector2.UnitX);
            bool flag3 = !CollideCheck<Player>(Position + Vector2.UnitX);
            bool flag4 = !CollideCheck<Player>(Position - Vector2.UnitY);
            if (flag2) {
                for (int i = 1; i < Height / 2 - 1; ++i) {
                    ParticleType particle = dreamParticles[activateParticleIndex];
                    Vector2 position = TopLeft + Vector2.UnitY * i * 2;
                    SceneAs<Level>().Particles.Emit(particle, 1, position, Vector2.One, (float) Math.PI);
                    ++activateParticleIndex;
                    activateParticleIndex %= 4;
                }
            }
            if (flag3) {
                for (int i = 1; i < Height / 2 - 1; ++i) {
                    ParticleType particle = dreamParticles[activateParticleIndex];
                    Vector2 position = TopRight + Vector2.UnitY * i * 2;
                    SceneAs<Level>().Particles.Emit(particle, 1, position, Vector2.One, 0f);
                    ++activateParticleIndex;
                    activateParticleIndex %= 4;
                }
            }
            if (flag4) {
                for (int i = 1; i < Width / 2 - 1; ++i) {
                    ParticleType particle = dreamParticles[activateParticleIndex];
                    Vector2 position = TopLeft + Vector2.UnitX * i * 2;
                    SceneAs<Level>().Particles.Emit(particle, 1, position, Vector2.One, -(float) Math.PI / 2f);
                    ++activateParticleIndex;
                    activateParticleIndex %= 4;
                }
            }
            for (int i = 1; i < Width / 2 - 1; ++i) {
                ParticleType particle = dreamParticles[activateParticleIndex];
                Vector2 position = BottomLeft + Vector2.UnitX * i * 2;
                SceneAs<Level>().Particles.Emit(particle, 1, position, Vector2.One, (float) Math.PI / 2f);
                ++activateParticleIndex;
                activateParticleIndex %= 4;
            }
        }

        private void BreakParticles() {
            Vector2 center = Center;
            for (int i = 0; i < Width; i += 4) {
                for (int j = 0; j < Height; j += 4) {
                    Vector2 vector = Position + new Vector2(2 + i, 2 + j);
                    SceneAs<Level>().Particles.Emit(dreamParticles[breakParticleIndex], 1, vector, Vector2.One * 2f, (vector - center).Angle());
                    ++breakParticleIndex;
                    breakParticleIndex %= 4;
                }
            }
        }

        private void MoveParticles() {
            Vector2 position;
            Vector2 positionRange;
            float num;
            float num2;
            if (direction == MoveBlock.Directions.Right) {
                position = CenterLeft + Vector2.UnitX;
                positionRange = Vector2.UnitY * (Height - 4f);
                num = (float) Math.PI;
                num2 = Height / 32f;
            } else if (direction == MoveBlock.Directions.Left) {
                position = CenterRight;
                positionRange = Vector2.UnitY * (Height - 4f);
                num = 0f;
                num2 = Height / 32f;
            } else if (direction == MoveBlock.Directions.Down) {
                position = TopCenter + Vector2.UnitY;
                positionRange = Vector2.UnitX * (Width - 4f);
                num = -(float) Math.PI / 2f;
                num2 = Width / 32f;
            } else {
                position = BottomCenter;
                positionRange = Vector2.UnitX * (Width - 4f);
                num = (float) Math.PI / 2f;
                num2 = Width / 32f;
            }
            particleRemainder += num2;
            int num3 = (int) particleRemainder;
            particleRemainder -= num3;
            positionRange *= 0.5f;
            if (num3 > 0) {
                SceneAs<Level>().ParticlesBG.Emit(dreamParticles[moveParticleIndex], num3, position, positionRange, num);
                ++moveParticleIndex;
                moveParticleIndex %= 4;
            }
        }

        private void ScrapeParticles(Vector2 dir) {
            if (noCollide)
                return;

            bool collidable = Collidable;
            Collidable = false;
            if (dir.X != 0f) {
                float x = (!(dir.X > 0f)) ? (Left - 1f) : Right;
                for (int i = 0; i < Height; i += 8) {
                    Vector2 vector = new Vector2(x, Top + 4f + i);
                    if (Scene.CollideCheck<Solid>(vector)) {
                        SceneAs<Level>().ParticlesFG.Emit(ZipMover.P_Scrape, vector);
                    }
                }
            } else {
                float y = (!(dir.Y > 0f)) ? (Top - 1f) : Bottom;
                for (int j = 0; j < Width; j += 8) {
                    Vector2 vector2 = new Vector2(Left + 4f + j, y);
                    if (Scene.CollideCheck<Solid>(vector2)) {
                        SceneAs<Level>().ParticlesFG.Emit(ZipMover.P_Scrape, vector2);
                    }
                }
            }
            Collidable = true;
        }

    }
}