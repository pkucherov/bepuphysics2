﻿using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection.SweepTasks;
using BepuUtilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace BepuPhysics.CollisionDetection.CollisionTasks
{
    public static class GradientDescent<TShapeA, TShapeWideA, TSupportFinderA, TShapeB, TShapeWideB, TSupportFinderB>
        where TShapeA : IConvexShape
        where TShapeWideA : IShapeWide<TShapeA>
        where TSupportFinderA : ISupportFinder<TShapeA, TShapeWideA>
        where TShapeB : IConvexShape
        where TShapeWideB : IShapeWide<TShapeB>
        where TSupportFinderB : ISupportFinder<TShapeB, TShapeWideB>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void FindSupport(in TShapeWideA a, in TShapeWideB b, in Vector3Wide localOffsetB, in Matrix3x3Wide localOrientationB, ref TSupportFinderA supportFinderA, ref TSupportFinderB supportFinderB, in Vector3Wide direction, out Vector3Wide support)
        {
            //support(N, A) - support(-N, B)
            supportFinderA.ComputeLocalSupport(a, direction, out var extremeA);
            Vector3Wide.Negate(direction, out var negatedDirection);
            supportFinderB.ComputeSupport(b, localOrientationB, negatedDirection, out var extremeB);
            Vector3Wide.Add(extremeB, localOffsetB, out extremeB);

            Vector3Wide.Subtract(extremeA, extremeB, out support);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void SampleDepth(in Vector3Wide normal, in Vector3Wide support,
            ref Vector3Wide minimumSupport, ref Vector3Wide minimumNormal, ref Vector<float> minimumDepthNumerator, ref Vector<float> minimumNormalLengthSquared, out Vector<int> usedNewSample)
        {
            Vector3Wide.Dot(normal, support, out var dot);
            var dotSquared = dot * dot;
            var depthNumerator = Vector.ConditionalSelect(Vector.LessThan(dot, Vector<float>.Zero), -dotSquared, dotSquared);
            Vector3Wide.LengthSquared(normal, out var normalLengthSquared);

            //var newDepth = ComputeDepth(depthNumerator, normalLengthSquared);
            //var oldDepth = ComputeDepth(minimumDepthNumerator, minimumNormalLengthSquared);

            //To compare two depths, just cross multiply:
            //sign(dot(N1, extremePoint1)) * dot(N1, extremePoint1) * ||N2||^2 <= sign(dot(N2, extremePoint2)) * dot(N2, extremePoint2) * ||N1||^2
            //(If we had a fast hardware rsqrt exposed by intrinsics, this could be avoided.)
            usedNewSample = Vector.LessThan(depthNumerator * minimumNormalLengthSquared, minimumDepthNumerator * normalLengthSquared);
            Vector3Wide.ConditionalSelect(usedNewSample, normal, minimumNormal, out minimumNormal);
            Vector3Wide.ConditionalSelect(usedNewSample, support, minimumSupport, out minimumSupport);
            minimumDepthNumerator = Vector.ConditionalSelect(usedNewSample, depthNumerator, minimumDepthNumerator);
            minimumNormalLengthSquared = Vector.ConditionalSelect(usedNewSample, normalLengthSquared, minimumNormalLengthSquared);
        }

        static Vector<float> ComputeDepth(in Vector<float> numerator, in Vector<float> normalLengthSquared)
        {
            var depth = Vector.SquareRoot(Vector.Abs(numerator) / normalLengthSquared);
            return Vector.ConditionalSelect(Vector.LessThan(numerator, Vector<float>.Zero), -depth, depth);
        }

        public static void Refine(
            in TShapeWideA a, in TShapeWideB b, in Vector3Wide localOffsetB, in Matrix3x3Wide localOrientationB,
            ref TSupportFinderA supportFinderA, ref TSupportFinderB supportFinderB, in Vector3Wide initialGuess, in Vector<float> terminationEpsilon, in Vector<int> inactiveLanes, out Vector3Wide localNormal, int maximumIterations = 15)
        {
            localNormal = initialGuess;

            //Despite not having any access to the underlying support function, we can treat it as max(p0, p1, p2... ) over all points in the convex shape.
            //So:
            //depth(N) = dot(N, extremePoint) 
            //d/dNTX(depth(N)) = dot(TX, extremePoint.X)
            //d/dNTY(depth(N)) = dot(TY, extremePoint.Y)
            //It's not a great gradient- it will have a lot of discontinuities, often right in the regions we are most interested in.
            //And the second derivative is just zero, so many other root finders are ruled out. But we can work with this.

            //We'll work on a 2d plane around the initial guess. 
            //This won't work well if the search ends up searching very far away, but we're expecting an initial guess within 60 degrees almost always (and often much closer than that).
            Helpers.BuildOrthnormalBasis(initialGuess, out var x, out var y);

            FindSupport(a, b, localOffsetB, localOrientationB, ref supportFinderA, ref supportFinderB, localNormal, out var minimumSupport);
            Vector2Wide gradient;
            Vector3Wide.Dot(minimumSupport, x, out gradient.X);
            Vector3Wide.Dot(minimumSupport, y, out gradient.Y);
            //We'll track the minimum depth across all iterations. This gradient descent will refuse to ever progress to a point which is worse.
            //To avoid divisions and square roots, the depth will be tracked without normalizing the normals at each step:
            //depth(N) = dot(N / ||N||, extremePoint) 
            //depth(N)^2 = dot(N, extremePoint) / ||N||^2
            //sign(dot(N, extremePoint) * depth(N)^2 = sign(dot(N, extremePoint)) * dot(N, extremePoint) / ||N||^2
            //So the squared normal length and the signed squared unnormalized depth are both tracked. 
            Vector3Wide.Dot(localNormal, minimumSupport, out var dot);
            var dotSquared = dot * dot;
            var minimumDepthNumerator = Vector.ConditionalSelect(Vector.LessThan(dot, Vector<float>.Zero), -dotSquared, dotSquared);
            //It is assumed that the initial guess is normalized- that's required by the basis calculation.
            Vector<float> minimumNormalLengthSquared = Vector<float>.One;

            //We want to pick a reasonable progression speed. The initial gradient magnitude can help us make that choice, and it allows us to detect the (unlikely) case that we already found the origin.
            Vector2Wide.Length(gradient, out var initialGradientMagnitude);
            var earlyOut = Vector.LessThan(initialGradientMagnitude, terminationEpsilon);
            if (Vector.LessThanAll(Vector.BitwiseOr(inactiveLanes, earlyOut), Vector<int>.Zero))
                return;
            var gradientScale = 0.25f * Vector.ConditionalSelect(earlyOut, Vector<float>.One, Vector<float>.One / initialGradientMagnitude);

            var terminationEpsilonSquared = terminationEpsilon * terminationEpsilon;
            for (int i = 0; i < maximumIterations; ++i)
            {
                //Create three sample locations from the previous location and the gradient. 
                //We'll sample in the direction of the (negative) gradient and two locations rotated 90 degrees away to capture cases where the gradient is stuck in a valley and bouncing off the walls.
                Vector3Wide normalForward;
                normalForward.X = localNormal.X - gradient.X * x.X - gradient.Y * y.X;
                normalForward.Y = localNormal.Y - gradient.X * x.Y - gradient.Y * y.Y;
                normalForward.Z = localNormal.Z - gradient.X * x.Z - gradient.Y * y.Z;
                Vector3Wide sideOffset;
                sideOffset.X = gradient.X * y.X - gradient.Y * x.X;
                sideOffset.Y = gradient.X * y.Y - gradient.Y * x.Y;
                sideOffset.Z = gradient.X * y.Z - gradient.Y * x.Z;
                Vector3Wide.Subtract(localNormal, sideOffset, out var normalSide0);
                Vector3Wide.Add(localNormal, sideOffset, out var normalSide1);
                FindSupport(a, b, localOffsetB, localOrientationB, ref supportFinderA, ref supportFinderB, normalForward, out var supportForward);
                SampleDepth(normalForward, supportForward, ref minimumSupport, ref localNormal, ref minimumDepthNumerator, ref minimumNormalLengthSquared, out var usedNewSampleForward);
                FindSupport(a, b, localOffsetB, localOrientationB, ref supportFinderA, ref supportFinderB, normalSide0, out var supportSide0);
                SampleDepth(normalSide0, supportSide0, ref minimumSupport, ref localNormal, ref minimumDepthNumerator, ref minimumNormalLengthSquared, out var usedNewSampleSide0);
                FindSupport(a, b, localOffsetB, localOrientationB, ref supportFinderA, ref supportFinderB, normalSide1, out var supportSide1);
                SampleDepth(normalSide1, supportSide1, ref minimumSupport, ref localNormal, ref minimumDepthNumerator, ref minimumNormalLengthSquared, out var usedNewSampleSide1);
                Vector3Wide.Dot(minimumSupport, x, out gradient.X);
                Vector3Wide.Dot(minimumSupport, y, out gradient.Y);


                //If any sample made progress for depth, then keep going with the current convergence speed. Otherwise, drop the scale heavily.
                var usedNewSample = Vector.BitwiseOr(usedNewSampleForward, Vector.BitwiseOr(usedNewSampleSide0, usedNewSampleSide1));
                gradientScale = Vector.ConditionalSelect(usedNewSample, gradientScale, gradientScale * 0.25f);
                gradient.X *= gradientScale;
                gradient.Y *= gradientScale;
                //If the remaining movement is small enough, just exit.
                Vector2Wide.Dot(gradient, gradient, out var gradientLengthSquared);
                var exit = Vector.BitwiseOr(inactiveLanes, Vector.LessThan(gradientLengthSquared, terminationEpsilonSquared));
                if (Vector.LessThanAll(exit, Vector<int>.Zero))
                {
                    break;
                }

            }
            var inverseNormalLength = Vector<float>.One / Vector.SquareRoot(minimumNormalLengthSquared);

            Vector3Wide.Scale(localNormal, inverseNormalLength, out localNormal);

        }

    }
}
