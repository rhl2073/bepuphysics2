﻿using BepuPhysics.CollisionDetection;
using BepuUtilities;
using BepuUtilities.Memory;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using static BepuUtilities.GatherScatter;
using Quaternion = BepuUtilities.Quaternion;

namespace BepuPhysics.Constraints
{
    /// <summary>
    /// Constrains two bodies to rotate around attached local axes at a target velocity at a target velocity.
    /// </summary>
    /// <remarks>
    /// This is pretty similar to the TwistMotor, but it doesn't take into account any changes in the axis direction. 
    /// In other words, if you rotate the bodies around axes other than their local constrained axes, the constraint doesn't see any motion at all.
    /// It's actually difficult to build a physical mechanism which matches the behavior of this constraint. It prioritizes simplicity and directness over plausibility.
    /// </remarks>
    public struct AngularAxisMotor : IConstraintDescription<AngularAxisMotor>
    {
        public Vector3 LocalAxisA;
        public Vector3 LocalAxisB;
        public float TargetVelocity;
        public MotorSettings Settings;

        public int ConstraintTypeId
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return AngularAxisMotorTypeProcessor.BatchTypeId;
            }
        }

        public Type BatchType => typeof(AngularAxisMotorTypeProcessor);

        public void ApplyDescription(ref TypeBatch batch, int bundleIndex, int innerIndex)
        {
            Debug.Assert(ConstraintTypeId == batch.TypeId, "The type batch passed to the description must match the description's expected type.");
            ref var target = ref GetOffsetInstance(ref Buffer<AngularAxisMotorPrestepData>.Get(ref batch.PrestepData, bundleIndex), innerIndex);
            Vector3Wide.WriteFirst(LocalAxisA, ref target.LocalAxisA);
            Vector3Wide.WriteFirst(LocalAxisB, ref target.LocalAxisB);
            GatherScatter.GetFirst(ref target.TargetVelocity) = TargetVelocity;
            MotorSettingsWide.WriteFirst(Settings, ref target.Settings);
        }

        public void BuildDescription(ref TypeBatch batch, int bundleIndex, int innerIndex, out AngularAxisMotor description)
        {
            Debug.Assert(ConstraintTypeId == batch.TypeId, "The type batch passed to the description must match the description's expected type.");
            ref var source = ref GetOffsetInstance(ref Buffer<AngularAxisMotorPrestepData>.Get(ref batch.PrestepData, bundleIndex), innerIndex);
            Vector3Wide.ReadFirst(source.LocalAxisA, out description.LocalAxisA);
            Vector3Wide.ReadFirst(source.LocalAxisB, out description.LocalAxisB);
            description.TargetVelocity = GatherScatter.GetFirst(ref source.TargetVelocity);
            MotorSettingsWide.ReadFirst(source.Settings, out description.Settings);
        }
    }

    public struct AngularAxisMotorPrestepData
    {
        public Vector3Wide LocalAxisA;
        public Vector3Wide LocalAxisB;
        public Vector<float> TargetVelocity;
        public MotorSettingsWide Settings;
    }

    public struct AngularAxisMotorProjection
    {
        public Vector3Wide VelocityToImpulseA;
        public Vector3Wide NegatedVelocityToImpulseB;
        public Vector<float> BiasImpulse;
        public Vector<float> SoftnessImpulseScale;
        public Vector<float> MaximumImpulse;
        public Vector3Wide ImpulseToVelocityA;
        public Vector3Wide NegatedImpulseToVelocityB;
    }


    public struct AngularAxisMotorFunctions : IConstraintFunctions<AngularAxisMotorPrestepData, AngularAxisMotorProjection, Vector<float>>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Prestep(Bodies bodies, ref TwoBodyReferences bodyReferences, int count, float dt, float inverseDt, ref BodyInertias inertiaA, ref BodyInertias inertiaB,
            ref AngularAxisMotorPrestepData prestep, out AngularAxisMotorProjection projection)
        {
            //Velocity level constraint that acts directly on the given axes. Jacobians just the axes, nothing complicated. 1DOF, so we do premultiplication.
            bodies.GatherOrientation(ref bodyReferences, count, out var orientationA, out var orientationB);
            QuaternionWide.TransformWithoutOverlap(prestep.LocalAxisA, orientationA, out var axisA);
            QuaternionWide.TransformWithoutOverlap(prestep.LocalAxisB, orientationB, out var axisB);
            Symmetric3x3Wide.TransformWithoutOverlap(axisA, inertiaA.InverseInertiaTensor, out projection.ImpulseToVelocityA);
            Symmetric3x3Wide.TransformWithoutOverlap(axisB, inertiaB.InverseInertiaTensor, out projection.NegatedImpulseToVelocityB);
            Vector3Wide.Dot(axisA, projection.ImpulseToVelocityA, out var contributionA);
            Vector3Wide.Dot(axisB, projection.NegatedImpulseToVelocityB, out var contributionB);
            MotorSettingsWide.ComputeSoftness(prestep.Settings, dt, out var effectiveMassCFMScale, out projection.SoftnessImpulseScale, out projection.MaximumImpulse);
            var effectiveMass = effectiveMassCFMScale / (contributionA + contributionB);

            Vector3Wide.Scale(axisA, effectiveMass, out projection.VelocityToImpulseA);
            Vector3Wide.Scale(axisB, effectiveMass, out projection.NegatedVelocityToImpulseB);

            projection.BiasImpulse = prestep.TargetVelocity * effectiveMass;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ApplyImpulse(ref Vector3Wide angularVelocityA, ref Vector3Wide angularVelocityB, in AngularAxisMotorProjection projection, in Vector<float> csi)
        {
            Vector3Wide.Scale(projection.ImpulseToVelocityA, csi, out var velocityChangeA);
            Vector3Wide.Scale(projection.NegatedImpulseToVelocityB, csi, out var negatedVelocityChangeB);
            Vector3Wide.Add(angularVelocityA, velocityChangeA, out angularVelocityA);
            Vector3Wide.Subtract(angularVelocityB, negatedVelocityChangeB, out angularVelocityB);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WarmStart(ref BodyVelocities velocityA, ref BodyVelocities velocityB, ref AngularAxisMotorProjection projection, ref Vector<float> accumulatedImpulse)
        {
            ApplyImpulse(ref velocityA.Angular, ref velocityB.Angular, projection, accumulatedImpulse);
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Solve(ref BodyVelocities velocityA, ref BodyVelocities velocityB, ref AngularAxisMotorProjection projection, ref Vector<float> accumulatedImpulse)
        {
            //csi = projection.BiasImpulse - accumulatedImpulse * projection.SoftnessImpulseScale - (csiaLinear + csiaAngular + csibLinear + csibAngular);
            Vector3Wide.Dot(velocityA.Angular, projection.VelocityToImpulseA, out var csiA);
            Vector3Wide.Dot(velocityB.Angular, projection.NegatedVelocityToImpulseB, out var negatedCSIB);
            var csi = projection.BiasImpulse - accumulatedImpulse * projection.SoftnessImpulseScale - (csiA - negatedCSIB);
            ServoSettingsWide.ClampImpulse(projection.MaximumImpulse, ref accumulatedImpulse, ref csi);
            ApplyImpulse(ref velocityA.Angular, ref velocityB.Angular, projection, csi);

        }

    }

    public class AngularAxisMotorTypeProcessor : TwoBodyTypeProcessor<AngularAxisMotorPrestepData, AngularAxisMotorProjection, Vector<float>, AngularAxisMotorFunctions>
    {
        public const int BatchTypeId = 41;
    }
}
