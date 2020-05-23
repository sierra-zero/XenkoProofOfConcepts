using BepuPhysics.Collidables;
using BepuPhysics.Constraints;
using BepuPhysicsExample.BepuPhysicsIntegration.Engine;
using System.Collections.Generic;
using System.ComponentModel;
using Xenko.Core;
using Xenko.Core.Annotations;
using Xenko.Core.Collections;
using Xenko.Core.Diagnostics;
using Xenko.Core.Mathematics;
using Xenko.Core.MicroThreading;
using Xenko.Engine;
using Xenko.Engine.Design;
using Xenko.Physics;
using Xenko.Rendering;

namespace BepuPhysicsExample.BepuPhysicsIntegration
{
    public enum CollisionState
    {
        Ignore,
        Detect
    }

    [DataContract("BepuPhysicsComponent", Inherited = true)]
    [Display("Physics (Bepu)", Expand = ExpandRule.Once)]
    [DefaultEntityComponentProcessor(typeof(BepuPhysicsProcessor))]
    [AllowMultipleComponents]
    [ComponentOrder(3010)]
    [ComponentCategory("Physics")]
    public abstract class BepuPhysicsComponent : ActivableEntityComponent
    {
        protected static Logger logger = GlobalLogger.GetLogger(nameof(BepuPhysicsComponent));

        private static readonly TypedIndex ShapeTypeIndexNotSet = default;

        static BepuPhysicsComponent()
        {
            // Preload proper libbulletc native library (depending on CPU type)
            //NativeLibrary.PreloadLibrary("libbulletc.dll", typeof(BepuPhysicsComponent));
        }

        protected BepuPhysicsComponent()
        {
            CanScaleShape = true;

            ColliderShapes = new ColliderShapeCollection(this);

            NewPairChannel = new Channel<BepuCollision> { Preference = ChannelPreference.PreferSender };
            PairEndedChannel = new Channel<BepuCollision> { Preference = ChannelPreference.PreferSender };
        }

        //[DataMemberIgnore]
        //[System.Obsolete]
        //internal BulletSharp.CollisionObject NativeCollisionObject;

        [DataMemberIgnore]
        internal CollidableReferenceKey NativeCollidableReference;

        /// <userdoc>
        /// The reference to the collider shape of this element.
        /// </userdoc>
        [DataMember(200)]
        [Category]
        [MemberCollection(NotNullItems = true)]
        public ColliderShapeCollection ColliderShapes { get; }

        /// <summary>
        /// Gets or sets the collision group.
        /// </summary>
        /// <value>
        /// The collision group.
        /// </value>
        /// <userdoc>
        /// Which collision group the component belongs to. This can't be changed at runtime. The default is DefaultFilter.
        /// </userdoc>
        /// <remarks>
        /// The collider will still produce events, to allow non trigger rigidbodies or static colliders to act as a trigger if required for certain filtering groups.
        /// </remarks>
        [DataMember(30)]
        [Display("Collision group")]
        [DefaultValue(CollisionFilterGroups.DefaultFilter)]
        public CollisionFilterGroups CollisionGroup { get; set; } = CollisionFilterGroups.DefaultFilter;

        /// <summary>
        /// Gets or sets the can collide with.
        /// </summary>
        /// <value>
        /// The can collide with.
        /// </value>
        /// <userdoc>
        /// Which collider groups this component collides with. With nothing selected, it collides with all groups. This can't be changed at runtime.
        /// </userdoc>
        /// /// <remarks>
        /// The collider will still produce events, to allow non trigger rigidbodies or static colliders to act as a trigger if required for certain filtering groups.
        /// </remarks>
        [DataMember(40)]
        [Display("Collides with...")]
        [DefaultValue(CollisionFilterGroupFlags.AllFilter)]
        public CollisionFilterGroupFlags CanCollideWith { get; set; } = CollisionFilterGroupFlags.AllFilter;

        /// <summary>
        /// Gets or sets if this element will store collisions
        /// </summary>
        /// <value>
        /// true, false
        /// </value>
        /// <userdoc>
        /// You can use collision events in scripts. If you have no scripts using collision events for this component, disable this option to save CPU. It has no effect on physics.
        /// </userdoc>
        [Display("Record collision events")]
        [DataMemberIgnore]
        public bool ProcessCollisions { get; set; } = false;

        /// <summary>
        /// Gets or sets if this element is enabled in the physics engine
        /// </summary>
        /// <value>
        /// true, false
        /// </value>
        /// <userdoc>
        /// If this element is enabled in the physics engine
        /// </userdoc>
        [DataMember(-10)]
        [DefaultValue(true)]
        public override bool Enabled
        {
            get
            {
                return base.Enabled;
            }
            set
            {
                base.Enabled = value;
                DebugEntity?.EnableAll(value, true);
            }
        }

        /// <summary>
        /// Radius of the margin in which to allow speculative contact generation.
        /// </summary>
        [DataMember(55)]
        [DefaultValue(0.1f)]
        public float SpeculativeMargin { get; set; } = 0.1f;

        /// <summary>
        /// Gets or sets if this element restitution
        /// </summary>
        /// <userdoc>
        /// The amount of kinetic energy lost or gained after a collision. If the restitution of colliding entities is 0, the entities lose all energy and stop moving immediately on impact. If the restitution is 1, they lose no energy and rebound with the same velocity they collided at. Use this to change the component "bounciness". A typical value is between 0 and 1.
        /// </userdoc>
        [DataMember(60)]
        public float Restitution { get; set; }

        /// <summary>
        /// Gets or sets the friction of this element
        /// </summary>
        /// <userdoc>
        /// The friction
        /// </userdoc>
        /// <remarks>
        /// It's important to realise that friction and restitution are not values of any particular surface, but rather a value of the interaction of two surfaces.
        /// So why is it defined for each object? In order to determine the overall friction and restitution between any two surfaces in a collision.
        /// </remarks>
        [DataMember(65)]
        [DefaultValue(0.5f)]
        public float Friction { get; set; } = 0.5f;

        /// <summary>
        /// Gets or sets the rolling friction of this element
        /// </summary>
        /// <userdoc>
        /// The rolling friction
        /// </userdoc>
        [DataMember(66)]
        public float RollingFriction { get; set; }

        [DataMember(67)]
        public float CcdMotionThreshold { get; set; }

        [DataMember(68)]
        public float CcdSweptSphereRadius { get; set; }


        [DataMember(80)]
        [DefaultValue(2f)]
        public float MaximumRecoveryVelocity { get; set; } = 2f;

        [DataMember(90)]
        public SpringSettings SpringSettings { get; set; } = new SpringSettings(30f, 20f);

        private Dictionary<BepuPhysicsComponent, CollisionState> ignoreCollisionBuffer;


        #region Ignore or Private/Internal

        [DataMemberIgnore]
        public TrackingHashSet<BepuCollision> Collisions { get; } = new TrackingHashSet<BepuCollision>();

        [DataMemberIgnore]
        internal Channel<BepuCollision> NewPairChannel;

        public ChannelMicroThreadAwaiter<BepuCollision> NewCollision()
        {
            return NewPairChannel.Receive();
        }

        [DataMemberIgnore]
        internal Channel<BepuCollision> PairEndedChannel;

        public ChannelMicroThreadAwaiter<BepuCollision> CollisionEnded()
        {
            return PairEndedChannel.Receive();
        }

        [DataMemberIgnore]
        public BepuSimulation Simulation { get; internal set; }

        [DataMemberIgnore]
        internal BepuPhysicsShapesRenderingService DebugShapeRendering;

        [DataMemberIgnore]
        public bool ColliderShapeChanged { get; private set; }

        [DataMemberIgnore]
        internal TypedIndex NativeShapeTypeIndex;

        [DataMemberIgnore]
        protected BepuColliderShape colliderShape;

        [DataMemberIgnore]
        public BepuColliderShape ColliderShape
        {
            get
            {
                return colliderShape;
            }
            set
            {
                var oldColliderShape = colliderShape;
                colliderShape = value;

                if (NativeShapeTypeIndex.Exists)
                {
                    Simulation.NativeBepuSimulation.Shapes.Remove(NativeShapeTypeIndex);
                    NativeShapeTypeIndex = ShapeTypeIndexNotSet;
                }

                // Overriding classes should set the new shape.
                OnColliderShapeChanged(oldColliderShape, newColliderShape: value);
            }
        }

        protected abstract void OnColliderShapeChanged(BepuColliderShape oldColliderShape, BepuColliderShape newColliderShape);

        [DataMemberIgnore]
        public bool CanScaleShape { get; set; }

        [DataMemberIgnore]
        public abstract Matrix PhysicsWorldTransform { get; set; }
        //{
        //    get
        //    {
        //        return NativeCollisionObject.WorldTransform;
        //    }
        //    set
        //    {
        //        NativeCollisionObject.WorldTransform = value;
        //    }
        //}

        /// <summary>
        /// Gets or sets the tag.
        /// </summary>
        /// <value>
        /// The tag.
        /// </value>
        [DataMemberIgnore]
        public string Tag { get; set; }

        [DataMemberIgnore]
        public Matrix BoneWorldMatrix;

        [DataMemberIgnore]
        public Matrix BoneWorldMatrixOut;

        [DataMemberIgnore]
        public int BoneIndex = -1;

        [DataMemberIgnore]
        protected BepuPhysicsProcessor.AssociatedData Data { get; set; }

        [DataMemberIgnore]
        public Entity DebugEntity { get; set; }

        public void AddDebugEntity(Scene scene, RenderGroup renderGroup = RenderGroup.Group0, bool alwaysAddOffset = false)
        {
            if (DebugEntity != null) return;

            var entity = Data?.PhysicsComponent?.DebugShapeRendering?.CreateDebugEntity(this, renderGroup, alwaysAddOffset);
            DebugEntity = entity;

            if (DebugEntity == null) return;

            scene.Entities.Add(entity);
        }

        public void RemoveDebugEntity(Scene scene)
        {
            if (DebugEntity == null) return;

            scene.Entities.Remove(DebugEntity);
            DebugEntity = null;
        }

        #endregion Ignore or Private/Internal

        #region Utility

        /// <summary>
        /// Computes the physics transformation from the TransformComponent values
        /// </summary>
        /// <returns></returns>
        internal void DerivePhysicsTransformation(out Matrix derivedTransformation)
        {
            Matrix rotation;
            Vector3 translation;
            Vector3 scale;
            Entity.Transform.WorldMatrix.Decompose(out scale, out rotation, out translation);

            var translationMatrix = Matrix.Translation(translation);
            Matrix.Multiply(ref rotation, ref translationMatrix, out derivedTransformation);

            // Handle dynamic scaling if allowed (aka not using assets)
            if (CanScaleShape)
            {
                if (ColliderShape.Scaling != scale)
                {
                    ColliderShape.Scaling = scale;
                }
            }

            // Handle collider shape offset
            if (ColliderShape.LocalOffset != Vector3.Zero || ColliderShape.LocalRotation != Quaternion.Identity)
            {
                derivedTransformation = Matrix.Multiply(ColliderShape.PositiveCenterMatrix, derivedTransformation);
            }

            if (DebugEntity == null) return;

            derivedTransformation.Decompose(out _, out rotation, out translation);
            DebugEntity.Transform.Position = translation;
            DebugEntity.Transform.Rotation = Quaternion.RotationMatrix(rotation);
        }

        /// <summary>
        /// Computes the physics transformation from the TransformComponent values
        /// </summary>
        /// <returns></returns>
        internal void DeriveBonePhysicsTransformation(out Matrix derivedTransformation)
        {
            Matrix rotation;
            Vector3 translation;
            Vector3 scale;
            BoneWorldMatrix.Decompose(out scale, out rotation, out translation);

            var translationMatrix = Matrix.Translation(translation);
            Matrix.Multiply(ref rotation, ref translationMatrix, out derivedTransformation);

            //handle dynamic scaling if allowed (aka not using assets)
            if (CanScaleShape)
            {
                if (ColliderShape.Scaling != scale)
                {
                    ColliderShape.Scaling = scale;
                }
            }

            //Handle collider shape offset
            if (ColliderShape.LocalOffset != Vector3.Zero || ColliderShape.LocalRotation != Quaternion.Identity)
            {
                derivedTransformation = Matrix.Multiply(ColliderShape.PositiveCenterMatrix, derivedTransformation);
            }

            if (DebugEntity == null) return;

            derivedTransformation.Decompose(out _, out rotation, out translation);
            DebugEntity.Transform.Position = translation;
            DebugEntity.Transform.Rotation = Quaternion.RotationMatrix(rotation);
        }

        /// <summary>
        /// Updates the entity's Transform component and the graphics transformation from the given physics transformation.
        /// </summary>
        /// <param name="physicsTransform"></param>
        internal void UpdateTransformationComponent(ref Matrix physicsTransform)
        {
            var entity = Entity;

            if (ColliderShape.LocalOffset != Vector3.Zero || ColliderShape.LocalRotation != Quaternion.Identity)
            {
                physicsTransform = Matrix.Multiply(ColliderShape.NegativeCenterMatrix, physicsTransform);
            }

            // We need to extract scale only..
            Vector3 scale, translation;
            Matrix rotation;
            entity.Transform.WorldMatrix.Decompose(out scale, out Matrix _, out _);

            var scaling = Matrix.Scaling(scale);
            Matrix.Multiply(ref scaling, ref physicsTransform, out entity.Transform.WorldMatrix);

            entity.Transform.UpdateLocalFromWorld();

            Quaternion rotQuat;
            entity.Transform.LocalMatrix.Decompose(out _, out rotQuat, out translation);
            entity.Transform.Position = translation;
            entity.Transform.Rotation = rotQuat;

            if (DebugEntity == null) return;

            if (ColliderShape.LocalOffset != Vector3.Zero || ColliderShape.LocalRotation != Quaternion.Identity)
            {
                physicsTransform = Matrix.Multiply(ColliderShape.PositiveCenterMatrix, physicsTransform);
            }

            physicsTransform.Decompose(out _, out rotation, out translation);
            DebugEntity.Transform.Position = translation;
            DebugEntity.Transform.Rotation = Quaternion.RotationMatrix(rotation);
        }

        /// <summary>
        /// Updates the entity's Transform component and the graphics transformation from the given physics transformation.
        /// </summary>
        /// <param name="physicsTransform"></param>
        internal void UpdateBoneTransformation(ref Matrix physicsTransform)
        {
            if (ColliderShape.LocalOffset != Vector3.Zero || ColliderShape.LocalRotation != Quaternion.Identity)
            {
                physicsTransform = Matrix.Multiply(ColliderShape.NegativeCenterMatrix, physicsTransform);
            }

            //we need to extract scale only..
            Vector3 scale, translation;
            Matrix rotation;
            BoneWorldMatrix.Decompose(out scale, out rotation, out translation);

            var scaling = Matrix.Scaling(scale);
            Matrix.Multiply(ref scaling, ref physicsTransform, out BoneWorldMatrixOut);

            //todo propagate to other bones? need to review this.

            if (DebugEntity == null) return;

            if (ColliderShape.LocalOffset != Vector3.Zero || ColliderShape.LocalRotation != Quaternion.Identity)
            {
                physicsTransform = Matrix.Multiply(ColliderShape.PositiveCenterMatrix, physicsTransform);
            }

            physicsTransform.Decompose(out scale, out rotation, out translation);
            DebugEntity.Transform.Position = translation;
            DebugEntity.Transform.Rotation = Quaternion.RotationMatrix(rotation);
        }

        /// <summary>
        /// Forces an update from the TransformComponent to the Collider.PhysicsWorldTransform.
        /// Useful to manually force movements.
        /// In the case of dynamic rigidbodies a velocity reset should be applied first.
        /// </summary>
        public void UpdatePhysicsTransformation()
        {
            Matrix transform;
            if (BoneIndex == -1)
            {
                DerivePhysicsTransformation(out transform);
            }
            else
            {
                DeriveBonePhysicsTransformation(out transform);
            }
            // Finally copy back to Bepu
            PhysicsWorldTransform = transform;
        }

        public void ComposeShape()
        {
            ColliderShapeChanged = false;

            if (ColliderShape != null)
            {
                if (!ColliderShape.IsPartOfAsset)
                {
                    ColliderShape.Dispose();
                    ColliderShape = null;
                }
                else
                {
                    ColliderShape = null;
                }
            }

            CanScaleShape = true;

            if (ColliderShapes.Count == 1) // Single shape case
            {
                if (ColliderShapes[0] == null) return;
                if (ColliderShapes[0].GetType() == typeof(BepuColliderShapeAssetDesc))
                {
                    CanScaleShape = false;
                }

                ColliderShape = BepuPhysicsColliderShape.CreateShape(ColliderShapes[0], Simulation.BufferPool);
            }
            else if (ColliderShapes.Count > 1) // Need a compound shape in this case
            {
                //var compound = new BepuCompoundColliderShape();
                //foreach (var desc in ColliderShapes)
                //{
                //    if (desc == null) continue;
                //    if (desc.GetType() == typeof(BepuColliderShapeAssetDesc))
                //    {
                //        CanScaleShape = false;
                //    }

                //    var subShape = BepuPhysicsColliderShape.CreateShape(desc);
                //    if (subShape != null)
                //    {
                //        compound.AddChildShape(subShape);
                //    }
                //}

                //ColliderShape = compound;
            }

            if (ColliderShape != null)
            {
                // Force update internal shape and gizmo scaling
                ColliderShape.Scaling = ColliderShape.Scaling;
            }
        }

        #endregion Utility

        internal void Attach(BepuPhysicsProcessor.AssociatedData data)
        {
            Data = data;

            // This is mostly required for the game studio gizmos
            if (BepuSimulation.DisableSimulation)
            {
                return;
            }

            // This is not optimal as UpdateWorldMatrix will end up being called twice this frame.. but we need to ensure that we have valid data.
            Entity.Transform.UpdateWorldMatrix();

            if (ColliderShapes.Count == 0 && ColliderShape == null)
            {
                logger.Error($"Entity {Entity.Name} has a PhysicsComponent without any collider shape.");
                return; //no shape no purpose
            }
            else if (ColliderShape == null)
            {
                ComposeShape();
                if (ColliderShape == null)
                {
                    logger.Error($"Entity {Entity.Name}'s PhysicsComponent failed to compose its collider shape.");
                    return; // No shape no purpose
                }
            }

            BoneIndex = -1;

            OnAttach();

            //if (ignoreCollisionBuffer != null && NativeCollisionObject != null)
            //{
            //    foreach (var kvp in ignoreCollisionBuffer)
            //    {
            //        IgnoreCollisionWith(kvp.Key, kvp.Value);
            //    }
            //    ignoreCollisionBuffer = null;
            //}
        }

        internal void Detach()
        {
            Data = null;

            // This is mostly required for the game studio gizmos
            if (BepuSimulation.DisableSimulation)
            {
                return;
            }

            // Actually call the detach
            OnDetach();

            if (ColliderShape != null && !ColliderShape.IsPartOfAsset)
            {
                ColliderShape.Dispose();
                ColliderShape = null;
            }
        }

        protected virtual void OnAttach()
        {
            // Set pre-set post deserialization properties
            Enabled = base.Enabled;
            //Restitution = restitution;
            //Friction = friction;
            //RollingFriction = rollingFriction;
            //CcdMotionThreshold = ccdMotionThreshold;
            //CcdSweptSphereRadius = ccdSweptSphereRadius;
        }

        protected virtual void OnDetach()
        {
            NativeCollidableReference = CollidableReferenceKey.NotSet;
        }

        internal void UpdateBones()
        {
            if (!Enabled)
            {
                return;
            }

            OnUpdateBones();
        }

        internal void UpdateDraw()
        {
            if (!Enabled)
            {
                return;
            }

            OnUpdateDraw();
        }

        protected internal virtual void OnUpdateDraw()
        {
        }

        protected virtual void OnUpdateBones()
        {
            // Read from ModelViewHierarchy
            var model = Data.ModelComponent;
            BoneWorldMatrix = model.Skeleton.NodeTransformations[BoneIndex].WorldMatrix;
        }

        public void IgnoreCollisionWith(BepuPhysicsComponent other, CollisionState state)
        {
            //var otherNative = other.NativeCollisionObject;
            //if (!NativeCollidableReference.IsSet || !other.NativeCollidableReference.IsSet)
            //{
            //    if (ignoreCollisionBuffer != null || other.ignoreCollisionBuffer == null)
            //    {
            //        if (ignoreCollisionBuffer == null)
            //            ignoreCollisionBuffer = new Dictionary<BepuPhysicsComponent, CollisionState>();
            //        if (ignoreCollisionBuffer.ContainsKey(other))
            //            ignoreCollisionBuffer[other] = state;
            //        else
            //            ignoreCollisionBuffer.Add(other, state);
            //    }
            //    else
            //    {
            //        other.IgnoreCollisionWith(this, state);
            //    }
            //    return;
            //}

            //switch (state)
            //{
            //    // Note that we're calling 'SetIgnoreCollisionCheck' on both objects as bullet doesn't
            //    // do it itself ; One of the object in the pair will report that it doesn't ignore
            //    // collision with the other even though you set the other as ignoring the former.
            //    case CollisionState.Ignore:
            //        {
            //            // Bullet uses an array per collision object to store all of the objects to ignore,
            //            // when calling this method it adds the referenced object without checking for duplicates,
            //            // so if a user where to call 'Ignore' of this function on this object n-times he'll have to call it
            //            // that same amount of time to re-detect them instead of just once.
            //            // We're calling false here to remove a previous ignore if there was any and re-ignoring
            //            // to force it to have only a single instance.
            //            otherNative.SetIgnoreCollisionCheck(NativeCollisionObject, false);
            //            NativeCollisionObject.SetIgnoreCollisionCheck(otherNative, false);
            //            otherNative.SetIgnoreCollisionCheck(NativeCollisionObject, true);
            //            NativeCollisionObject.SetIgnoreCollisionCheck(otherNative, true);
            //            break;
            //        }
            //    case CollisionState.Detect:
            //        {
            //            otherNative.SetIgnoreCollisionCheck(NativeCollisionObject, false);
            //            NativeCollisionObject.SetIgnoreCollisionCheck(otherNative, false);
            //            break;
            //        }
            //}
        }

        public bool IsIgnoringCollisionWith(BepuPhysicsComponent other)
        {
            if (ignoreCollisionBuffer != null)
            {
                return ignoreCollisionBuffer.TryGetValue(other, out var state) && state == CollisionState.Ignore;
            }
            else if (other.ignoreCollisionBuffer != null)
            {
                return other.IsIgnoringCollisionWith(this);
            }
            return false;
        }

        [DataContract]
        public class ColliderShapeCollection : FastCollection<IBepuInlineColliderShapeDesc>
        {
            BepuPhysicsComponent component;

            public ColliderShapeCollection(BepuPhysicsComponent componentParam)
            {
                component = componentParam;
            }

            /// <inheritdoc/>
            protected override void InsertItem(int index, IBepuInlineColliderShapeDesc item)
            {
                base.InsertItem(index, item);
                component.ColliderShapeChanged = true;
            }

            /// <inheritdoc/>
            protected override void RemoveItem(int index)
            {
                base.RemoveItem(index);
                component.ColliderShapeChanged = true;
            }

            /// <inheritdoc/>
            protected override void ClearItems()
            {
                base.ClearItems();
                component.ColliderShapeChanged = true;
            }

            /// <inheritdoc/>
            protected override void SetItem(int index, IBepuInlineColliderShapeDesc item)
            {
                base.SetItem(index, item);
                component.ColliderShapeChanged = true;
            }
        }
    }
}
