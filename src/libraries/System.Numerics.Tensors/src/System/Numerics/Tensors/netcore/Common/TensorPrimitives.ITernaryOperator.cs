﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace System.Numerics.Tensors
{
    public static unsafe partial class TensorPrimitives
    {
        /// <summary>Operator that takes three input values and returns a single value.</summary>
        private interface ITernaryOperator<T>
        {
            static abstract bool Vectorizable { get; }
            static abstract T Invoke(T x, T y, T z);
            static abstract Vector128<T> Invoke(Vector128<T> x, Vector128<T> y, Vector128<T> z);
            static abstract Vector256<T> Invoke(Vector256<T> x, Vector256<T> y, Vector256<T> z);
            static abstract Vector512<T> Invoke(Vector512<T> x, Vector512<T> y, Vector512<T> z);
        }

        private readonly struct SwappedYZTernaryOperator<TOperator, T> : ITernaryOperator<T>
            where TOperator : struct, ITernaryOperator<T>
        {
            public static bool Vectorizable => TOperator.Vectorizable;
            public static T Invoke(T x, T y, T z) => TOperator.Invoke(x, z, y);
            public static Vector128<T> Invoke(Vector128<T> x, Vector128<T> y, Vector128<T> z) => TOperator.Invoke(x, z, y);
            public static Vector256<T> Invoke(Vector256<T> x, Vector256<T> y, Vector256<T> z) => TOperator.Invoke(x, z, y);
            public static Vector512<T> Invoke(Vector512<T> x, Vector512<T> y, Vector512<T> z) => TOperator.Invoke(x, z, y);
        }

        /// <summary>
        /// Performs an element-wise operation on <paramref name="x"/>, <paramref name="y"/>, and <paramref name="z"/>,
        /// and writes the results to <paramref name="destination"/>.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <typeparam name="TTernaryOperator">
        /// Specifies the operation to perform on the pair-wise elements loaded from <paramref name="x"/>, <paramref name="y"/>,
        /// and <paramref name="z"/>.
        /// </typeparam>
        private static void InvokeSpanSpanSpanIntoSpan<T, TTernaryOperator>(
            ReadOnlySpan<T> x, ReadOnlySpan<T> y, ReadOnlySpan<T> z, Span<T> destination)
            where TTernaryOperator : struct, ITernaryOperator<T>
        {
            if (x.Length != y.Length || x.Length != z.Length)
            {
                ThrowHelper.ThrowArgument_SpansMustHaveSameLength();
            }

            if (x.Length > destination.Length)
            {
                ThrowHelper.ThrowArgument_DestinationTooShort();
            }

            ValidateInputOutputSpanNonOverlapping(x, destination);
            ValidateInputOutputSpanNonOverlapping(y, destination);
            ValidateInputOutputSpanNonOverlapping(z, destination);

            // Since every branch has a cost and since that cost is
            // essentially lost for larger inputs, we do branches
            // in a way that allows us to have the minimum possible
            // for small sizes

            ref T xRef = ref MemoryMarshal.GetReference(x);
            ref T yRef = ref MemoryMarshal.GetReference(y);
            ref T zRef = ref MemoryMarshal.GetReference(z);
            ref T dRef = ref MemoryMarshal.GetReference(destination);

            nuint remainder = (uint)x.Length;

            if (TTernaryOperator.Vectorizable && Vector512.IsHardwareAccelerated && Vector512<T>.IsSupported)
            {
                if (remainder >= (uint)Vector512<T>.Count)
                {
                    Vectorized512(ref xRef, ref yRef, ref zRef, ref dRef, remainder);
                }
                else
                {
                    // We have less than a vector and so we can only handle this as scalar. To do this
                    // efficiently, we simply have a small jump table and fallthrough. So we get a simple
                    // length check, single jump, and then linear execution.

                    VectorizedSmall(ref xRef, ref yRef, ref zRef, ref dRef, remainder);
                }

                return;
            }

            if (TTernaryOperator.Vectorizable && Vector256.IsHardwareAccelerated && Vector256<T>.IsSupported)
            {
                if (remainder >= (uint)Vector256<T>.Count)
                {
                    Vectorized256(ref xRef, ref yRef, ref zRef, ref dRef, remainder);
                }
                else
                {
                    // We have less than a vector and so we can only handle this as scalar. To do this
                    // efficiently, we simply have a small jump table and fallthrough. So we get a simple
                    // length check, single jump, and then linear execution.

                    VectorizedSmall(ref xRef, ref yRef, ref zRef, ref dRef, remainder);
                }

                return;
            }

            if (TTernaryOperator.Vectorizable && Vector128.IsHardwareAccelerated && Vector128<T>.IsSupported)
            {
                if (remainder >= (uint)Vector128<T>.Count)
                {
                    Vectorized128(ref xRef, ref yRef, ref zRef, ref dRef, remainder);
                }
                else
                {
                    // We have less than a vector and so we can only handle this as scalar. To do this
                    // efficiently, we simply have a small jump table and fallthrough. So we get a simple
                    // length check, single jump, and then linear execution.

                    VectorizedSmall(ref xRef, ref yRef, ref zRef, ref dRef, remainder);
                }

                return;
            }

            // This is the software fallback when no acceleration is available
            // It requires no branches to hit

            SoftwareFallback(ref xRef, ref yRef, ref zRef, ref dRef, remainder);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void SoftwareFallback(ref T xRef, ref T yRef, ref T zRef, ref T dRef, nuint length)
            {
                for (nuint i = 0; i < length; i++)
                {
                    Unsafe.Add(ref dRef, i) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, i),
                                                                      Unsafe.Add(ref yRef, i),
                                                                      Unsafe.Add(ref zRef, i));
                }
            }

            static void Vectorized128(ref T xRef, ref T yRef, ref T zRef, ref T dRef, nuint remainder)
            {
                ref T dRefBeg = ref dRef;

                // Preload the beginning and end so that overlapping accesses don't negatively impact the data

                Vector128<T> beg = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef),
                                                           Vector128.LoadUnsafe(ref yRef),
                                                           Vector128.LoadUnsafe(ref zRef));
                Vector128<T> end = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)Vector128<T>.Count),
                                                           Vector128.LoadUnsafe(ref yRef, remainder - (uint)Vector128<T>.Count),
                                                           Vector128.LoadUnsafe(ref zRef, remainder - (uint)Vector128<T>.Count));

                if (remainder > (uint)(Vector128<T>.Count * 8))
                {
                    // Pinning is cheap and will be short lived for small inputs and unlikely to be impactful
                    // for large inputs (> 85KB) which are on the LOH and unlikely to be compacted.

                    fixed (T* px = &xRef)
                    fixed (T* py = &yRef)
                    fixed (T* pz = &zRef)
                    fixed (T* pd = &dRef)
                    {
                        T* xPtr = px;
                        T* yPtr = py;
                        T* zPtr = pz;
                        T* dPtr = pd;

                        // We need to the ensure the underlying data can be aligned and only align
                        // it if it can. It is possible we have an unaligned ref, in which case we
                        // can never achieve the required SIMD alignment.

                        bool canAlign = ((nuint)dPtr % (nuint)sizeof(T)) == 0;

                        if (canAlign)
                        {
                            // Compute by how many elements we're misaligned and adjust the pointers accordingly
                            //
                            // Noting that we are only actually aligning dPtr. This is because unaligned stores
                            // are more expensive than unaligned loads and aligning both is significantly more
                            // complex.

                            nuint misalignment = ((uint)sizeof(Vector128<T>) - ((nuint)dPtr % (uint)sizeof(Vector128<T>))) / (uint)sizeof(T);

                            xPtr += misalignment;
                            yPtr += misalignment;
                            zPtr += misalignment;
                            dPtr += misalignment;

                            Debug.Assert(((nuint)dPtr % (uint)sizeof(Vector128<T>)) == 0);

                            remainder -= misalignment;
                        }

                        Vector128<T> vector1;
                        Vector128<T> vector2;
                        Vector128<T> vector3;
                        Vector128<T> vector4;

                        if ((remainder > (NonTemporalByteThreshold / (nuint)sizeof(T))) && canAlign)
                        {
                            // This loop stores the data non-temporally, which benefits us when there
                            // is a large amount of data involved as it avoids polluting the cache.

                            while (remainder >= (uint)(Vector128<T>.Count * 8))
                            {
                                // We load, process, and store the first four vectors

                                vector1 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 0)),
                                                                  Vector128.Load(yPtr + (uint)(Vector128<T>.Count * 0)),
                                                                  Vector128.Load(zPtr + (uint)(Vector128<T>.Count * 0)));
                                vector2 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 1)),
                                                                  Vector128.Load(yPtr + (uint)(Vector128<T>.Count * 1)),
                                                                  Vector128.Load(zPtr + (uint)(Vector128<T>.Count * 1)));
                                vector3 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 2)),
                                                                  Vector128.Load(yPtr + (uint)(Vector128<T>.Count * 2)),
                                                                  Vector128.Load(zPtr + (uint)(Vector128<T>.Count * 2)));
                                vector4 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 3)),
                                                                  Vector128.Load(yPtr + (uint)(Vector128<T>.Count * 3)),
                                                                  Vector128.Load(zPtr + (uint)(Vector128<T>.Count * 3)));

                                vector1.StoreAlignedNonTemporal(dPtr + (uint)(Vector128<T>.Count * 0));
                                vector2.StoreAlignedNonTemporal(dPtr + (uint)(Vector128<T>.Count * 1));
                                vector3.StoreAlignedNonTemporal(dPtr + (uint)(Vector128<T>.Count * 2));
                                vector4.StoreAlignedNonTemporal(dPtr + (uint)(Vector128<T>.Count * 3));

                                // We load, process, and store the next four vectors

                                vector1 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 4)),
                                                                  Vector128.Load(yPtr + (uint)(Vector128<T>.Count * 4)),
                                                                  Vector128.Load(zPtr + (uint)(Vector128<T>.Count * 4)));
                                vector2 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 5)),
                                                                  Vector128.Load(yPtr + (uint)(Vector128<T>.Count * 5)),
                                                                  Vector128.Load(zPtr + (uint)(Vector128<T>.Count * 5)));
                                vector3 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 6)),
                                                                  Vector128.Load(yPtr + (uint)(Vector128<T>.Count * 6)),
                                                                  Vector128.Load(zPtr + (uint)(Vector128<T>.Count * 6)));
                                vector4 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 7)),
                                                                  Vector128.Load(yPtr + (uint)(Vector128<T>.Count * 7)),
                                                                  Vector128.Load(zPtr + (uint)(Vector128<T>.Count * 7)));

                                vector1.StoreAlignedNonTemporal(dPtr + (uint)(Vector128<T>.Count * 4));
                                vector2.StoreAlignedNonTemporal(dPtr + (uint)(Vector128<T>.Count * 5));
                                vector3.StoreAlignedNonTemporal(dPtr + (uint)(Vector128<T>.Count * 6));
                                vector4.StoreAlignedNonTemporal(dPtr + (uint)(Vector128<T>.Count * 7));

                                // We adjust the source and destination references, then update
                                // the count of remaining elements to process.

                                xPtr += (uint)(Vector128<T>.Count * 8);
                                yPtr += (uint)(Vector128<T>.Count * 8);
                                zPtr += (uint)(Vector128<T>.Count * 8);
                                dPtr += (uint)(Vector128<T>.Count * 8);

                                remainder -= (uint)(Vector128<T>.Count * 8);
                            }
                        }
                        else
                        {
                            while (remainder >= (uint)(Vector128<T>.Count * 8))
                            {
                                // We load, process, and store the first four vectors

                                vector1 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 0)),
                                                                  Vector128.Load(yPtr + (uint)(Vector128<T>.Count * 0)),
                                                                  Vector128.Load(zPtr + (uint)(Vector128<T>.Count * 0)));
                                vector2 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 1)),
                                                                  Vector128.Load(yPtr + (uint)(Vector128<T>.Count * 1)),
                                                                  Vector128.Load(zPtr + (uint)(Vector128<T>.Count * 1)));
                                vector3 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 2)),
                                                                  Vector128.Load(yPtr + (uint)(Vector128<T>.Count * 2)),
                                                                  Vector128.Load(zPtr + (uint)(Vector128<T>.Count * 2)));
                                vector4 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 3)),
                                                                  Vector128.Load(yPtr + (uint)(Vector128<T>.Count * 3)),
                                                                  Vector128.Load(zPtr + (uint)(Vector128<T>.Count * 3)));

                                vector1.Store(dPtr + (uint)(Vector128<T>.Count * 0));
                                vector2.Store(dPtr + (uint)(Vector128<T>.Count * 1));
                                vector3.Store(dPtr + (uint)(Vector128<T>.Count * 2));
                                vector4.Store(dPtr + (uint)(Vector128<T>.Count * 3));

                                // We load, process, and store the next four vectors

                                vector1 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 4)),
                                                                  Vector128.Load(yPtr + (uint)(Vector128<T>.Count * 4)),
                                                                  Vector128.Load(zPtr + (uint)(Vector128<T>.Count * 4)));
                                vector2 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 5)),
                                                                  Vector128.Load(yPtr + (uint)(Vector128<T>.Count * 5)),
                                                                  Vector128.Load(zPtr + (uint)(Vector128<T>.Count * 5)));
                                vector3 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 6)),
                                                                  Vector128.Load(yPtr + (uint)(Vector128<T>.Count * 6)),
                                                                  Vector128.Load(zPtr + (uint)(Vector128<T>.Count * 6)));
                                vector4 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 7)),
                                                                  Vector128.Load(yPtr + (uint)(Vector128<T>.Count * 7)),
                                                                  Vector128.Load(zPtr + (uint)(Vector128<T>.Count * 7)));

                                vector1.Store(dPtr + (uint)(Vector128<T>.Count * 4));
                                vector2.Store(dPtr + (uint)(Vector128<T>.Count * 5));
                                vector3.Store(dPtr + (uint)(Vector128<T>.Count * 6));
                                vector4.Store(dPtr + (uint)(Vector128<T>.Count * 7));

                                // We adjust the source and destination references, then update
                                // the count of remaining elements to process.

                                xPtr += (uint)(Vector128<T>.Count * 8);
                                yPtr += (uint)(Vector128<T>.Count * 8);
                                zPtr += (uint)(Vector128<T>.Count * 8);
                                dPtr += (uint)(Vector128<T>.Count * 8);

                                remainder -= (uint)(Vector128<T>.Count * 8);
                            }
                        }

                        // Adjusting the refs here allows us to avoid pinning for very small inputs

                        xRef = ref *xPtr;
                        yRef = ref *yPtr;
                        zRef = ref *zPtr;
                        dRef = ref *dPtr;
                    }
                }

                // Process the remaining [Count, Count * 8] elements via a jump table
                //
                // Unless the original length was an exact multiple of Count, then we'll
                // end up reprocessing a couple elements in case 1 for end. We'll also
                // potentially reprocess a few elements in case 0 for beg, to handle any
                // data before the first aligned address.

                nuint endIndex = remainder;
                remainder = (remainder + (uint)(Vector128<T>.Count - 1)) & (nuint)(-Vector128<T>.Count);

                switch (remainder / (uint)Vector128<T>.Count)
                {
                    case 8:
                        {
                            Vector128<T> vector = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)(Vector128<T>.Count * 8)),
                                                                          Vector128.LoadUnsafe(ref yRef, remainder - (uint)(Vector128<T>.Count * 8)),
                                                                          Vector128.LoadUnsafe(ref zRef, remainder - (uint)(Vector128<T>.Count * 8)));
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector128<T>.Count * 8));
                            goto case 7;
                        }

                    case 7:
                        {
                            Vector128<T> vector = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)(Vector128<T>.Count * 7)),
                                                                          Vector128.LoadUnsafe(ref yRef, remainder - (uint)(Vector128<T>.Count * 7)),
                                                                          Vector128.LoadUnsafe(ref zRef, remainder - (uint)(Vector128<T>.Count * 7)));
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector128<T>.Count * 7));
                            goto case 6;
                        }

                    case 6:
                        {
                            Vector128<T> vector = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)(Vector128<T>.Count * 6)),
                                                                          Vector128.LoadUnsafe(ref yRef, remainder - (uint)(Vector128<T>.Count * 6)),
                                                                          Vector128.LoadUnsafe(ref zRef, remainder - (uint)(Vector128<T>.Count * 6)));
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector128<T>.Count * 6));
                            goto case 5;
                        }

                    case 5:
                        {
                            Vector128<T> vector = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)(Vector128<T>.Count * 5)),
                                                                          Vector128.LoadUnsafe(ref yRef, remainder - (uint)(Vector128<T>.Count * 5)),
                                                                          Vector128.LoadUnsafe(ref zRef, remainder - (uint)(Vector128<T>.Count * 5)));
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector128<T>.Count * 5));
                            goto case 4;
                        }

                    case 4:
                        {
                            Vector128<T> vector = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)(Vector128<T>.Count * 4)),
                                                                          Vector128.LoadUnsafe(ref yRef, remainder - (uint)(Vector128<T>.Count * 4)),
                                                                          Vector128.LoadUnsafe(ref zRef, remainder - (uint)(Vector128<T>.Count * 4)));
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector128<T>.Count * 4));
                            goto case 3;
                        }

                    case 3:
                        {
                            Vector128<T> vector = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)(Vector128<T>.Count * 3)),
                                                                          Vector128.LoadUnsafe(ref yRef, remainder - (uint)(Vector128<T>.Count * 3)),
                                                                          Vector128.LoadUnsafe(ref zRef, remainder - (uint)(Vector128<T>.Count * 3)));
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector128<T>.Count * 3));
                            goto case 2;
                        }

                    case 2:
                        {
                            Vector128<T> vector = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)(Vector128<T>.Count * 2)),
                                                                          Vector128.LoadUnsafe(ref yRef, remainder - (uint)(Vector128<T>.Count * 2)),
                                                                          Vector128.LoadUnsafe(ref zRef, remainder - (uint)(Vector128<T>.Count * 2)));
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector128<T>.Count * 2));
                            goto case 1;
                        }

                    case 1:
                        {
                            // Store the last block, which includes any elements that wouldn't fill a full vector
                            end.StoreUnsafe(ref dRef, endIndex - (uint)Vector128<T>.Count);
                            goto case 0;
                        }

                    case 0:
                        {
                            // Store the first block, which includes any elements preceding the first aligned block
                            beg.StoreUnsafe(ref dRefBeg);
                            break;
                        }
                }
            }

            static void Vectorized256(ref T xRef, ref T yRef, ref T zRef, ref T dRef, nuint remainder)
            {
                ref T dRefBeg = ref dRef;

                // Preload the beginning and end so that overlapping accesses don't negatively impact the data

                Vector256<T> beg = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef),
                                                           Vector256.LoadUnsafe(ref yRef),
                                                           Vector256.LoadUnsafe(ref zRef));
                Vector256<T> end = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)Vector256<T>.Count),
                                                           Vector256.LoadUnsafe(ref yRef, remainder - (uint)Vector256<T>.Count),
                                                           Vector256.LoadUnsafe(ref zRef, remainder - (uint)Vector256<T>.Count));

                if (remainder > (uint)(Vector256<T>.Count * 8))
                {
                    // Pinning is cheap and will be short lived for small inputs and unlikely to be impactful
                    // for large inputs (> 85KB) which are on the LOH and unlikely to be compacted.

                    fixed (T* px = &xRef)
                    fixed (T* py = &yRef)
                    fixed (T* pz = &zRef)
                    fixed (T* pd = &dRef)
                    {
                        T* xPtr = px;
                        T* yPtr = py;
                        T* zPtr = pz;
                        T* dPtr = pd;

                        // We need to the ensure the underlying data can be aligned and only align
                        // it if it can. It is possible we have an unaligned ref, in which case we
                        // can never achieve the required SIMD alignment.

                        bool canAlign = ((nuint)dPtr % (nuint)sizeof(T)) == 0;

                        if (canAlign)
                        {
                            // Compute by how many elements we're misaligned and adjust the pointers accordingly
                            //
                            // Noting that we are only actually aligning dPtr. This is because unaligned stores
                            // are more expensive than unaligned loads and aligning both is significantly more
                            // complex.

                            nuint misalignment = ((uint)sizeof(Vector256<T>) - ((nuint)dPtr % (uint)sizeof(Vector256<T>))) / (nuint)sizeof(T);

                            xPtr += misalignment;
                            yPtr += misalignment;
                            zPtr += misalignment;
                            dPtr += misalignment;

                            Debug.Assert(((nuint)dPtr % (uint)sizeof(Vector256<T>)) == 0);

                            remainder -= misalignment;
                        }

                        Vector256<T> vector1;
                        Vector256<T> vector2;
                        Vector256<T> vector3;
                        Vector256<T> vector4;

                        if ((remainder > (NonTemporalByteThreshold / (nuint)sizeof(T))) && canAlign)
                        {
                            // This loop stores the data non-temporally, which benefits us when there
                            // is a large amount of data involved as it avoids polluting the cache.

                            while (remainder >= (uint)(Vector256<T>.Count * 8))
                            {
                                // We load, process, and store the first four vectors

                                vector1 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 0)),
                                                                  Vector256.Load(yPtr + (uint)(Vector256<T>.Count * 0)),
                                                                  Vector256.Load(zPtr + (uint)(Vector256<T>.Count * 0)));
                                vector2 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 1)),
                                                                  Vector256.Load(yPtr + (uint)(Vector256<T>.Count * 1)),
                                                                  Vector256.Load(zPtr + (uint)(Vector256<T>.Count * 1)));
                                vector3 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 2)),
                                                                  Vector256.Load(yPtr + (uint)(Vector256<T>.Count * 2)),
                                                                  Vector256.Load(zPtr + (uint)(Vector256<T>.Count * 2)));
                                vector4 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 3)),
                                                                  Vector256.Load(yPtr + (uint)(Vector256<T>.Count * 3)),
                                                                  Vector256.Load(zPtr + (uint)(Vector256<T>.Count * 3)));

                                vector1.StoreAlignedNonTemporal(dPtr + (uint)(Vector256<T>.Count * 0));
                                vector2.StoreAlignedNonTemporal(dPtr + (uint)(Vector256<T>.Count * 1));
                                vector3.StoreAlignedNonTemporal(dPtr + (uint)(Vector256<T>.Count * 2));
                                vector4.StoreAlignedNonTemporal(dPtr + (uint)(Vector256<T>.Count * 3));

                                // We load, process, and store the next four vectors

                                vector1 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 4)),
                                                                  Vector256.Load(yPtr + (uint)(Vector256<T>.Count * 4)),
                                                                  Vector256.Load(zPtr + (uint)(Vector256<T>.Count * 4)));
                                vector2 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 5)),
                                                                  Vector256.Load(yPtr + (uint)(Vector256<T>.Count * 5)),
                                                                  Vector256.Load(zPtr + (uint)(Vector256<T>.Count * 5)));
                                vector3 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 6)),
                                                                  Vector256.Load(yPtr + (uint)(Vector256<T>.Count * 6)),
                                                                  Vector256.Load(zPtr + (uint)(Vector256<T>.Count * 6)));
                                vector4 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 7)),
                                                                  Vector256.Load(yPtr + (uint)(Vector256<T>.Count * 7)),
                                                                  Vector256.Load(zPtr + (uint)(Vector256<T>.Count * 7)));

                                vector1.StoreAlignedNonTemporal(dPtr + (uint)(Vector256<T>.Count * 4));
                                vector2.StoreAlignedNonTemporal(dPtr + (uint)(Vector256<T>.Count * 5));
                                vector3.StoreAlignedNonTemporal(dPtr + (uint)(Vector256<T>.Count * 6));
                                vector4.StoreAlignedNonTemporal(dPtr + (uint)(Vector256<T>.Count * 7));

                                // We adjust the source and destination references, then update
                                // the count of remaining elements to process.

                                xPtr += (uint)(Vector256<T>.Count * 8);
                                yPtr += (uint)(Vector256<T>.Count * 8);
                                zPtr += (uint)(Vector256<T>.Count * 8);
                                dPtr += (uint)(Vector256<T>.Count * 8);

                                remainder -= (uint)(Vector256<T>.Count * 8);
                            }
                        }
                        else
                        {
                            while (remainder >= (uint)(Vector256<T>.Count * 8))
                            {
                                // We load, process, and store the first four vectors

                                vector1 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 0)),
                                                                  Vector256.Load(yPtr + (uint)(Vector256<T>.Count * 0)),
                                                                  Vector256.Load(zPtr + (uint)(Vector256<T>.Count * 0)));
                                vector2 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 1)),
                                                                  Vector256.Load(yPtr + (uint)(Vector256<T>.Count * 1)),
                                                                  Vector256.Load(zPtr + (uint)(Vector256<T>.Count * 1)));
                                vector3 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 2)),
                                                                  Vector256.Load(yPtr + (uint)(Vector256<T>.Count * 2)),
                                                                  Vector256.Load(zPtr + (uint)(Vector256<T>.Count * 2)));
                                vector4 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 3)),
                                                                  Vector256.Load(yPtr + (uint)(Vector256<T>.Count * 3)),
                                                                  Vector256.Load(zPtr + (uint)(Vector256<T>.Count * 3)));

                                vector1.Store(dPtr + (uint)(Vector256<T>.Count * 0));
                                vector2.Store(dPtr + (uint)(Vector256<T>.Count * 1));
                                vector3.Store(dPtr + (uint)(Vector256<T>.Count * 2));
                                vector4.Store(dPtr + (uint)(Vector256<T>.Count * 3));

                                // We load, process, and store the next four vectors

                                vector1 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 4)),
                                                                  Vector256.Load(yPtr + (uint)(Vector256<T>.Count * 4)),
                                                                  Vector256.Load(zPtr + (uint)(Vector256<T>.Count * 4)));
                                vector2 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 5)),
                                                                  Vector256.Load(yPtr + (uint)(Vector256<T>.Count * 5)),
                                                                  Vector256.Load(zPtr + (uint)(Vector256<T>.Count * 5)));
                                vector3 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 6)),
                                                                  Vector256.Load(yPtr + (uint)(Vector256<T>.Count * 6)),
                                                                  Vector256.Load(zPtr + (uint)(Vector256<T>.Count * 6)));
                                vector4 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 7)),
                                                                  Vector256.Load(yPtr + (uint)(Vector256<T>.Count * 7)),
                                                                  Vector256.Load(zPtr + (uint)(Vector256<T>.Count * 7)));

                                vector1.Store(dPtr + (uint)(Vector256<T>.Count * 4));
                                vector2.Store(dPtr + (uint)(Vector256<T>.Count * 5));
                                vector3.Store(dPtr + (uint)(Vector256<T>.Count * 6));
                                vector4.Store(dPtr + (uint)(Vector256<T>.Count * 7));

                                // We adjust the source and destination references, then update
                                // the count of remaining elements to process.

                                xPtr += (uint)(Vector256<T>.Count * 8);
                                yPtr += (uint)(Vector256<T>.Count * 8);
                                zPtr += (uint)(Vector256<T>.Count * 8);
                                dPtr += (uint)(Vector256<T>.Count * 8);

                                remainder -= (uint)(Vector256<T>.Count * 8);
                            }
                        }

                        // Adjusting the refs here allows us to avoid pinning for very small inputs

                        xRef = ref *xPtr;
                        yRef = ref *yPtr;
                        zRef = ref *zPtr;
                        dRef = ref *dPtr;
                    }
                }

                // Process the remaining [Count, Count * 8] elements via a jump table
                //
                // Unless the original length was an exact multiple of Count, then we'll
                // end up reprocessing a couple elements in case 1 for end. We'll also
                // potentially reprocess a few elements in case 0 for beg, to handle any
                // data before the first aligned address.

                nuint endIndex = remainder;
                remainder = (remainder + (uint)(Vector256<T>.Count - 1)) & (nuint)(-Vector256<T>.Count);

                switch (remainder / (uint)Vector256<T>.Count)
                {
                    case 8:
                        {
                            Vector256<T> vector = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)(Vector256<T>.Count * 8)),
                                                                          Vector256.LoadUnsafe(ref yRef, remainder - (uint)(Vector256<T>.Count * 8)),
                                                                          Vector256.LoadUnsafe(ref zRef, remainder - (uint)(Vector256<T>.Count * 8)));
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector256<T>.Count * 8));
                            goto case 7;
                        }

                    case 7:
                        {
                            Vector256<T> vector = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)(Vector256<T>.Count * 7)),
                                                                          Vector256.LoadUnsafe(ref yRef, remainder - (uint)(Vector256<T>.Count * 7)),
                                                                          Vector256.LoadUnsafe(ref zRef, remainder - (uint)(Vector256<T>.Count * 7)));
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector256<T>.Count * 7));
                            goto case 6;
                        }

                    case 6:
                        {
                            Vector256<T> vector = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)(Vector256<T>.Count * 6)),
                                                                          Vector256.LoadUnsafe(ref yRef, remainder - (uint)(Vector256<T>.Count * 6)),
                                                                          Vector256.LoadUnsafe(ref zRef, remainder - (uint)(Vector256<T>.Count * 6)));
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector256<T>.Count * 6));
                            goto case 5;
                        }

                    case 5:
                        {
                            Vector256<T> vector = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)(Vector256<T>.Count * 5)),
                                                                          Vector256.LoadUnsafe(ref yRef, remainder - (uint)(Vector256<T>.Count * 5)),
                                                                          Vector256.LoadUnsafe(ref zRef, remainder - (uint)(Vector256<T>.Count * 5)));
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector256<T>.Count * 5));
                            goto case 4;
                        }

                    case 4:
                        {
                            Vector256<T> vector = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)(Vector256<T>.Count * 4)),
                                                                          Vector256.LoadUnsafe(ref yRef, remainder - (uint)(Vector256<T>.Count * 4)),
                                                                          Vector256.LoadUnsafe(ref zRef, remainder - (uint)(Vector256<T>.Count * 4)));
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector256<T>.Count * 4));
                            goto case 3;
                        }

                    case 3:
                        {
                            Vector256<T> vector = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)(Vector256<T>.Count * 3)),
                                                                          Vector256.LoadUnsafe(ref yRef, remainder - (uint)(Vector256<T>.Count * 3)),
                                                                          Vector256.LoadUnsafe(ref zRef, remainder - (uint)(Vector256<T>.Count * 3)));
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector256<T>.Count * 3));
                            goto case 2;
                        }

                    case 2:
                        {
                            Vector256<T> vector = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)(Vector256<T>.Count * 2)),
                                                                          Vector256.LoadUnsafe(ref yRef, remainder - (uint)(Vector256<T>.Count * 2)),
                                                                          Vector256.LoadUnsafe(ref zRef, remainder - (uint)(Vector256<T>.Count * 2)));
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector256<T>.Count * 2));
                            goto case 1;
                        }

                    case 1:
                        {
                            // Store the last block, which includes any elements that wouldn't fill a full vector
                            end.StoreUnsafe(ref dRef, endIndex - (uint)Vector256<T>.Count);
                            goto case 0;
                        }

                    case 0:
                        {
                            // Store the first block, which includes any elements preceding the first aligned block
                            beg.StoreUnsafe(ref dRefBeg);
                            break;
                        }
                }
            }

            static void Vectorized512(ref T xRef, ref T yRef, ref T zRef, ref T dRef, nuint remainder)
            {
                ref T dRefBeg = ref dRef;

                // Preload the beginning and end so that overlapping accesses don't negatively impact the data

                Vector512<T> beg = TTernaryOperator.Invoke(Vector512.LoadUnsafe(ref xRef),
                                                           Vector512.LoadUnsafe(ref yRef),
                                                           Vector512.LoadUnsafe(ref zRef));
                Vector512<T> end = TTernaryOperator.Invoke(Vector512.LoadUnsafe(ref xRef, remainder - (uint)Vector512<T>.Count),
                                                           Vector512.LoadUnsafe(ref yRef, remainder - (uint)Vector512<T>.Count),
                                                           Vector512.LoadUnsafe(ref zRef, remainder - (uint)Vector512<T>.Count));

                if (remainder > (uint)(Vector512<T>.Count * 8))
                {
                    // Pinning is cheap and will be short lived for small inputs and unlikely to be impactful
                    // for large inputs (> 85KB) which are on the LOH and unlikely to be compacted.

                    fixed (T* px = &xRef)
                    fixed (T* py = &yRef)
                    fixed (T* pz = &zRef)
                    fixed (T* pd = &dRef)
                    {
                        T* xPtr = px;
                        T* yPtr = py;
                        T* zPtr = pz;
                        T* dPtr = pd;

                        // We need to the ensure the underlying data can be aligned and only align
                        // it if it can. It is possible we have an unaligned ref, in which case we
                        // can never achieve the required SIMD alignment.

                        bool canAlign = ((nuint)dPtr % (nuint)sizeof(T)) == 0;

                        if (canAlign)
                        {
                            // Compute by how many elements we're misaligned and adjust the pointers accordingly
                            //
                            // Noting that we are only actually aligning dPtr. This is because unaligned stores
                            // are more expensive than unaligned loads and aligning both is significantly more
                            // complex.

                            nuint misalignment = ((uint)sizeof(Vector512<T>) - ((nuint)dPtr % (uint)sizeof(Vector512<T>))) / (uint)sizeof(T);

                            xPtr += misalignment;
                            yPtr += misalignment;
                            zPtr += misalignment;
                            dPtr += misalignment;

                            Debug.Assert(((nuint)dPtr % (uint)sizeof(Vector512<T>)) == 0);

                            remainder -= misalignment;
                        }

                        Vector512<T> vector1;
                        Vector512<T> vector2;
                        Vector512<T> vector3;
                        Vector512<T> vector4;

                        if ((remainder > (NonTemporalByteThreshold / (nuint)sizeof(T))) && canAlign)
                        {
                            // This loop stores the data non-temporally, which benefits us when there
                            // is a large amount of data involved as it avoids polluting the cache.

                            while (remainder >= (uint)(Vector512<T>.Count * 8))
                            {
                                // We load, process, and store the first four vectors

                                vector1 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 0)),
                                                                  Vector512.Load(yPtr + (uint)(Vector512<T>.Count * 0)),
                                                                  Vector512.Load(zPtr + (uint)(Vector512<T>.Count * 0)));
                                vector2 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 1)),
                                                                  Vector512.Load(yPtr + (uint)(Vector512<T>.Count * 1)),
                                                                  Vector512.Load(zPtr + (uint)(Vector512<T>.Count * 1)));
                                vector3 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 2)),
                                                                  Vector512.Load(yPtr + (uint)(Vector512<T>.Count * 2)),
                                                                  Vector512.Load(zPtr + (uint)(Vector512<T>.Count * 2)));
                                vector4 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 3)),
                                                                  Vector512.Load(yPtr + (uint)(Vector512<T>.Count * 3)),
                                                                  Vector512.Load(zPtr + (uint)(Vector512<T>.Count * 3)));

                                vector1.StoreAlignedNonTemporal(dPtr + (uint)(Vector512<T>.Count * 0));
                                vector2.StoreAlignedNonTemporal(dPtr + (uint)(Vector512<T>.Count * 1));
                                vector3.StoreAlignedNonTemporal(dPtr + (uint)(Vector512<T>.Count * 2));
                                vector4.StoreAlignedNonTemporal(dPtr + (uint)(Vector512<T>.Count * 3));

                                // We load, process, and store the next four vectors

                                vector1 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 4)),
                                                                  Vector512.Load(yPtr + (uint)(Vector512<T>.Count * 4)),
                                                                  Vector512.Load(zPtr + (uint)(Vector512<T>.Count * 4)));
                                vector2 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 5)),
                                                                  Vector512.Load(yPtr + (uint)(Vector512<T>.Count * 5)),
                                                                  Vector512.Load(zPtr + (uint)(Vector512<T>.Count * 5)));
                                vector3 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 6)),
                                                                  Vector512.Load(yPtr + (uint)(Vector512<T>.Count * 6)),
                                                                  Vector512.Load(zPtr + (uint)(Vector512<T>.Count * 6)));
                                vector4 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 7)),
                                                                  Vector512.Load(yPtr + (uint)(Vector512<T>.Count * 7)),
                                                                  Vector512.Load(zPtr + (uint)(Vector512<T>.Count * 7)));

                                vector1.StoreAlignedNonTemporal(dPtr + (uint)(Vector512<T>.Count * 4));
                                vector2.StoreAlignedNonTemporal(dPtr + (uint)(Vector512<T>.Count * 5));
                                vector3.StoreAlignedNonTemporal(dPtr + (uint)(Vector512<T>.Count * 6));
                                vector4.StoreAlignedNonTemporal(dPtr + (uint)(Vector512<T>.Count * 7));

                                // We adjust the source and destination references, then update
                                // the count of remaining elements to process.

                                xPtr += (uint)(Vector512<T>.Count * 8);
                                yPtr += (uint)(Vector512<T>.Count * 8);
                                zPtr += (uint)(Vector512<T>.Count * 8);
                                dPtr += (uint)(Vector512<T>.Count * 8);

                                remainder -= (uint)(Vector512<T>.Count * 8);
                            }
                        }
                        else
                        {
                            while (remainder >= (uint)(Vector512<T>.Count * 8))
                            {
                                // We load, process, and store the first four vectors

                                vector1 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 0)),
                                                                  Vector512.Load(yPtr + (uint)(Vector512<T>.Count * 0)),
                                                                  Vector512.Load(zPtr + (uint)(Vector512<T>.Count * 0)));
                                vector2 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 1)),
                                                                  Vector512.Load(yPtr + (uint)(Vector512<T>.Count * 1)),
                                                                  Vector512.Load(zPtr + (uint)(Vector512<T>.Count * 1)));
                                vector3 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 2)),
                                                                  Vector512.Load(yPtr + (uint)(Vector512<T>.Count * 2)),
                                                                  Vector512.Load(zPtr + (uint)(Vector512<T>.Count * 2)));
                                vector4 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 3)),
                                                                  Vector512.Load(yPtr + (uint)(Vector512<T>.Count * 3)),
                                                                  Vector512.Load(zPtr + (uint)(Vector512<T>.Count * 3)));

                                vector1.Store(dPtr + (uint)(Vector512<T>.Count * 0));
                                vector2.Store(dPtr + (uint)(Vector512<T>.Count * 1));
                                vector3.Store(dPtr + (uint)(Vector512<T>.Count * 2));
                                vector4.Store(dPtr + (uint)(Vector512<T>.Count * 3));

                                // We load, process, and store the next four vectors

                                vector1 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 4)),
                                                                  Vector512.Load(yPtr + (uint)(Vector512<T>.Count * 4)),
                                                                  Vector512.Load(zPtr + (uint)(Vector512<T>.Count * 4)));
                                vector2 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 5)),
                                                                  Vector512.Load(yPtr + (uint)(Vector512<T>.Count * 5)),
                                                                  Vector512.Load(zPtr + (uint)(Vector512<T>.Count * 5)));
                                vector3 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 6)),
                                                                  Vector512.Load(yPtr + (uint)(Vector512<T>.Count * 6)),
                                                                  Vector512.Load(zPtr + (uint)(Vector512<T>.Count * 6)));
                                vector4 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 7)),
                                                                  Vector512.Load(yPtr + (uint)(Vector512<T>.Count * 7)),
                                                                  Vector512.Load(zPtr + (uint)(Vector512<T>.Count * 7)));

                                vector1.Store(dPtr + (uint)(Vector512<T>.Count * 4));
                                vector2.Store(dPtr + (uint)(Vector512<T>.Count * 5));
                                vector3.Store(dPtr + (uint)(Vector512<T>.Count * 6));
                                vector4.Store(dPtr + (uint)(Vector512<T>.Count * 7));

                                // We adjust the source and destination references, then update
                                // the count of remaining elements to process.

                                xPtr += (uint)(Vector512<T>.Count * 8);
                                yPtr += (uint)(Vector512<T>.Count * 8);
                                zPtr += (uint)(Vector512<T>.Count * 8);
                                dPtr += (uint)(Vector512<T>.Count * 8);

                                remainder -= (uint)(Vector512<T>.Count * 8);
                            }
                        }

                        // Adjusting the refs here allows us to avoid pinning for very small inputs

                        xRef = ref *xPtr;
                        yRef = ref *yPtr;
                        zRef = ref *zPtr;
                        dRef = ref *dPtr;
                    }
                }

                // Process the remaining [Count, Count * 8] elements via a jump table
                //
                // Unless the original length was an exact multiple of Count, then we'll
                // end up reprocessing a couple elements in case 1 for end. We'll also
                // potentially reprocess a few elements in case 0 for beg, to handle any
                // data before the first aligned address.

                nuint endIndex = remainder;
                remainder = (remainder + (uint)(Vector512<T>.Count - 1)) & (nuint)(-Vector512<T>.Count);

                switch (remainder / (uint)Vector512<T>.Count)
                {
                    case 8:
                        {
                            Vector512<T> vector = TTernaryOperator.Invoke(Vector512.LoadUnsafe(ref xRef, remainder - (uint)(Vector512<T>.Count * 8)),
                                                                          Vector512.LoadUnsafe(ref yRef, remainder - (uint)(Vector512<T>.Count * 8)),
                                                                          Vector512.LoadUnsafe(ref zRef, remainder - (uint)(Vector512<T>.Count * 8)));
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector512<T>.Count * 8));
                            goto case 7;
                        }

                    case 7:
                        {
                            Vector512<T> vector = TTernaryOperator.Invoke(Vector512.LoadUnsafe(ref xRef, remainder - (uint)(Vector512<T>.Count * 7)),
                                                                          Vector512.LoadUnsafe(ref yRef, remainder - (uint)(Vector512<T>.Count * 7)),
                                                                          Vector512.LoadUnsafe(ref zRef, remainder - (uint)(Vector512<T>.Count * 7)));
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector512<T>.Count * 7));
                            goto case 6;
                        }

                    case 6:
                        {
                            Vector512<T> vector = TTernaryOperator.Invoke(Vector512.LoadUnsafe(ref xRef, remainder - (uint)(Vector512<T>.Count * 6)),
                                                                          Vector512.LoadUnsafe(ref yRef, remainder - (uint)(Vector512<T>.Count * 6)),
                                                                          Vector512.LoadUnsafe(ref zRef, remainder - (uint)(Vector512<T>.Count * 6)));
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector512<T>.Count * 6));
                            goto case 5;
                        }

                    case 5:
                        {
                            Vector512<T> vector = TTernaryOperator.Invoke(Vector512.LoadUnsafe(ref xRef, remainder - (uint)(Vector512<T>.Count * 5)),
                                                                          Vector512.LoadUnsafe(ref yRef, remainder - (uint)(Vector512<T>.Count * 5)),
                                                                          Vector512.LoadUnsafe(ref zRef, remainder - (uint)(Vector512<T>.Count * 5)));
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector512<T>.Count * 5));
                            goto case 4;
                        }

                    case 4:
                        {
                            Vector512<T> vector = TTernaryOperator.Invoke(Vector512.LoadUnsafe(ref xRef, remainder - (uint)(Vector512<T>.Count * 4)),
                                                                          Vector512.LoadUnsafe(ref yRef, remainder - (uint)(Vector512<T>.Count * 4)),
                                                                          Vector512.LoadUnsafe(ref zRef, remainder - (uint)(Vector512<T>.Count * 4)));
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector512<T>.Count * 4));
                            goto case 3;
                        }

                    case 3:
                        {
                            Vector512<T> vector = TTernaryOperator.Invoke(Vector512.LoadUnsafe(ref xRef, remainder - (uint)(Vector512<T>.Count * 3)),
                                                                          Vector512.LoadUnsafe(ref yRef, remainder - (uint)(Vector512<T>.Count * 3)),
                                                                          Vector512.LoadUnsafe(ref zRef, remainder - (uint)(Vector512<T>.Count * 3)));
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector512<T>.Count * 3));
                            goto case 2;
                        }

                    case 2:
                        {
                            Vector512<T> vector = TTernaryOperator.Invoke(Vector512.LoadUnsafe(ref xRef, remainder - (uint)(Vector512<T>.Count * 2)),
                                                                          Vector512.LoadUnsafe(ref yRef, remainder - (uint)(Vector512<T>.Count * 2)),
                                                                          Vector512.LoadUnsafe(ref zRef, remainder - (uint)(Vector512<T>.Count * 2)));
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector512<T>.Count * 2));
                            goto case 1;
                        }

                    case 1:
                        {
                            // Store the last block, which includes any elements that wouldn't fill a full vector
                            end.StoreUnsafe(ref dRef, endIndex - (uint)Vector512<T>.Count);
                            goto case 0;
                        }

                    case 0:
                        {
                            // Store the first block, which includes any elements preceding the first aligned block
                            beg.StoreUnsafe(ref dRefBeg);
                            break;
                        }
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void VectorizedSmall(ref T xRef, ref T yRef, ref T zRef, ref T dRef, nuint remainder)
            {
                if (sizeof(T) == 1)
                {
                    VectorizedSmall1(ref xRef, ref yRef, ref zRef, ref dRef, remainder);
                }
                else if (sizeof(T) == 2)
                {
                    VectorizedSmall2(ref xRef, ref yRef, ref zRef, ref dRef, remainder);
                }
                else if (sizeof(T) == 4)
                {
                    VectorizedSmall4(ref xRef, ref yRef, ref zRef, ref dRef, remainder);
                }
                else
                {
                    Debug.Assert(sizeof(T) == 8);
                    VectorizedSmall8(ref xRef, ref yRef, ref zRef, ref dRef, remainder);
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void VectorizedSmall1(ref T xRef, ref T yRef, ref T zRef, ref T dRef, nuint remainder)
            {
                Debug.Assert(sizeof(T) == 1);

                switch (remainder)
                {
                    // Two Vector256's worth of data, with at least one element overlapping.
                    case 63:
                    case 62:
                    case 61:
                    case 60:
                    case 59:
                    case 58:
                    case 57:
                    case 56:
                    case 55:
                    case 54:
                    case 53:
                    case 52:
                    case 51:
                    case 50:
                    case 49:
                    case 48:
                    case 47:
                    case 46:
                    case 45:
                    case 44:
                    case 43:
                    case 42:
                    case 41:
                    case 40:
                    case 39:
                    case 38:
                    case 37:
                    case 36:
                    case 35:
                    case 34:
                    case 33:
                        {
                            Debug.Assert(Vector256.IsHardwareAccelerated);

                            Vector256<T> beg = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef),
                                                                       Vector256.LoadUnsafe(ref yRef),
                                                                       Vector256.LoadUnsafe(ref zRef));
                            Vector256<T> end = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)Vector256<T>.Count),
                                                                       Vector256.LoadUnsafe(ref yRef, remainder - (uint)Vector256<T>.Count),
                                                                       Vector256.LoadUnsafe(ref zRef, remainder - (uint)Vector256<T>.Count));

                            beg.StoreUnsafe(ref dRef);
                            end.StoreUnsafe(ref dRef, remainder - (uint)Vector256<T>.Count);

                            break;
                        }

                    // One Vector256's worth of data.
                    case 32:
                        {
                            Debug.Assert(Vector256.IsHardwareAccelerated);

                            Vector256<T> beg = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef),
                                                                           Vector256.LoadUnsafe(ref yRef),
                                                                           Vector256.LoadUnsafe(ref zRef));
                            beg.StoreUnsafe(ref dRef);

                            break;
                        }

                    // Two Vector128's worth of data, with at least one element overlapping.
                    case 31:
                    case 30:
                    case 29:
                    case 28:
                    case 27:
                    case 26:
                    case 25:
                    case 24:
                    case 23:
                    case 22:
                    case 21:
                    case 20:
                    case 19:
                    case 18:
                    case 17:
                        {
                            Debug.Assert(Vector128.IsHardwareAccelerated);

                            Vector128<T> beg = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef),
                                                                           Vector128.LoadUnsafe(ref yRef),
                                                                           Vector128.LoadUnsafe(ref zRef));
                            Vector128<T> end = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)Vector128<T>.Count),
                                                                           Vector128.LoadUnsafe(ref yRef, remainder - (uint)Vector128<T>.Count),
                                                                           Vector128.LoadUnsafe(ref zRef, remainder - (uint)Vector128<T>.Count));

                            beg.StoreUnsafe(ref dRef);
                            end.StoreUnsafe(ref dRef, remainder - (uint)Vector128<T>.Count);

                            break;
                        }

                    // One Vector128's worth of data.
                    case 16:
                        {
                            Debug.Assert(Vector128.IsHardwareAccelerated);

                            Vector128<T> beg = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef),
                                                                           Vector128.LoadUnsafe(ref yRef),
                                                                           Vector128.LoadUnsafe(ref zRef));
                            beg.StoreUnsafe(ref dRef);

                            break;
                        }

                    // Cases that are smaller than a single vector. No SIMD; just jump to the length and fall through each
                    // case to unroll the whole processing.
                    case 15:
                        Unsafe.Add(ref dRef, 14) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 14),
                                                                          Unsafe.Add(ref yRef, 14),
                                                                          Unsafe.Add(ref zRef, 14));
                        goto case 14;

                    case 14:
                        Unsafe.Add(ref dRef, 13) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 13),
                                                                          Unsafe.Add(ref yRef, 13),
                                                                          Unsafe.Add(ref zRef, 13));
                        goto case 13;

                    case 13:
                        Unsafe.Add(ref dRef, 12) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 12),
                                                                          Unsafe.Add(ref yRef, 12),
                                                                          Unsafe.Add(ref zRef, 12));
                        goto case 12;

                    case 12:
                        Unsafe.Add(ref dRef, 11) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 11),
                                                                          Unsafe.Add(ref yRef, 11),
                                                                          Unsafe.Add(ref zRef, 11));
                        goto case 11;

                    case 11:
                        Unsafe.Add(ref dRef, 10) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 10),
                                                                          Unsafe.Add(ref yRef, 10),
                                                                          Unsafe.Add(ref zRef, 10));
                        goto case 10;

                    case 10:
                        Unsafe.Add(ref dRef, 9) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 9),
                                                                          Unsafe.Add(ref yRef, 9),
                                                                          Unsafe.Add(ref zRef, 9));
                        goto case 9;

                    case 9:
                        Unsafe.Add(ref dRef, 8) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 8),
                                                                          Unsafe.Add(ref yRef, 8),
                                                                          Unsafe.Add(ref zRef, 8));
                        goto case 8;

                    case 8:
                        Unsafe.Add(ref dRef, 7) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 7),
                                                                          Unsafe.Add(ref yRef, 7),
                                                                          Unsafe.Add(ref zRef, 7));
                        goto case 7;

                    case 7:
                        Unsafe.Add(ref dRef, 6) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 6),
                                                                          Unsafe.Add(ref yRef, 6),
                                                                          Unsafe.Add(ref zRef, 6));
                        goto case 6;

                    case 6:
                        Unsafe.Add(ref dRef, 5) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 5),
                                                                          Unsafe.Add(ref yRef, 5),
                                                                          Unsafe.Add(ref zRef, 5));
                        goto case 5;

                    case 5:
                        Unsafe.Add(ref dRef, 4) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 4),
                                                                          Unsafe.Add(ref yRef, 4),
                                                                          Unsafe.Add(ref zRef, 4));
                        goto case 4;

                    case 4:
                        Unsafe.Add(ref dRef, 3) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 3),
                                                                          Unsafe.Add(ref yRef, 3),
                                                                          Unsafe.Add(ref zRef, 3));
                        goto case 3;

                    case 3:
                        Unsafe.Add(ref dRef, 2) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 2),
                                                                          Unsafe.Add(ref yRef, 2),
                                                                          Unsafe.Add(ref zRef, 2));
                        goto case 2;

                    case 2:
                        Unsafe.Add(ref dRef, 1) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 1),
                                                                          Unsafe.Add(ref yRef, 1),
                                                                          Unsafe.Add(ref zRef, 1));
                        goto case 1;

                    case 1:
                        dRef = TTernaryOperator.Invoke(xRef, yRef, zRef);
                        goto case 0;

                    case 0:
                        break;
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void VectorizedSmall2(ref T xRef, ref T yRef, ref T zRef, ref T dRef, nuint remainder)
            {
                Debug.Assert(sizeof(T) == 2);

                switch (remainder)
                {
                    // Two Vector256's worth of data, with at least one element overlapping.
                    case 31:
                    case 30:
                    case 29:
                    case 28:
                    case 27:
                    case 26:
                    case 25:
                    case 24:
                    case 23:
                    case 22:
                    case 21:
                    case 20:
                    case 19:
                    case 18:
                    case 17:
                        {
                            Debug.Assert(Vector256.IsHardwareAccelerated);

                            Vector256<T> beg = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef),
                                                                       Vector256.LoadUnsafe(ref yRef),
                                                                       Vector256.LoadUnsafe(ref zRef));
                            Vector256<T> end = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)Vector256<T>.Count),
                                                                       Vector256.LoadUnsafe(ref yRef, remainder - (uint)Vector256<T>.Count),
                                                                       Vector256.LoadUnsafe(ref zRef, remainder - (uint)Vector256<T>.Count));

                            beg.StoreUnsafe(ref dRef);
                            end.StoreUnsafe(ref dRef, remainder - (uint)Vector256<T>.Count);

                            break;
                        }

                    // One Vector256's worth of data.
                    case 16:
                        {
                            Debug.Assert(Vector256.IsHardwareAccelerated);

                            Vector256<T> beg = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef),
                                                                           Vector256.LoadUnsafe(ref yRef),
                                                                           Vector256.LoadUnsafe(ref zRef));
                            beg.StoreUnsafe(ref dRef);

                            break;
                        }

                    // Two Vector128's worth of data, with at least one element overlapping.
                    case 15:
                    case 14:
                    case 13:
                    case 12:
                    case 11:
                    case 10:
                    case 9:
                        {
                            Debug.Assert(Vector128.IsHardwareAccelerated);

                            Vector128<T> beg = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef),
                                                                           Vector128.LoadUnsafe(ref yRef),
                                                                           Vector128.LoadUnsafe(ref zRef));
                            Vector128<T> end = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)Vector128<T>.Count),
                                                                           Vector128.LoadUnsafe(ref yRef, remainder - (uint)Vector128<T>.Count),
                                                                           Vector128.LoadUnsafe(ref zRef, remainder - (uint)Vector128<T>.Count));

                            beg.StoreUnsafe(ref dRef);
                            end.StoreUnsafe(ref dRef, remainder - (uint)Vector128<T>.Count);

                            break;
                        }

                    // One Vector128's worth of data.
                    case 8:
                        {
                            Debug.Assert(Vector128.IsHardwareAccelerated);

                            Vector128<T> beg = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef),
                                                                           Vector128.LoadUnsafe(ref yRef),
                                                                           Vector128.LoadUnsafe(ref zRef));
                            beg.StoreUnsafe(ref dRef);

                            break;
                        }

                    // Cases that are smaller than a single vector. No SIMD; just jump to the length and fall through each
                    // case to unroll the whole processing.
                    case 7:
                        Unsafe.Add(ref dRef, 6) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 6),
                                                                          Unsafe.Add(ref yRef, 6),
                                                                          Unsafe.Add(ref zRef, 6));
                        goto case 6;

                    case 6:
                        Unsafe.Add(ref dRef, 5) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 5),
                                                                          Unsafe.Add(ref yRef, 5),
                                                                          Unsafe.Add(ref zRef, 5));
                        goto case 5;

                    case 5:
                        Unsafe.Add(ref dRef, 4) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 4),
                                                                          Unsafe.Add(ref yRef, 4),
                                                                          Unsafe.Add(ref zRef, 4));
                        goto case 4;

                    case 4:
                        Unsafe.Add(ref dRef, 3) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 3),
                                                                          Unsafe.Add(ref yRef, 3),
                                                                          Unsafe.Add(ref zRef, 3));
                        goto case 3;

                    case 3:
                        Unsafe.Add(ref dRef, 2) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 2),
                                                                          Unsafe.Add(ref yRef, 2),
                                                                          Unsafe.Add(ref zRef, 2));
                        goto case 2;

                    case 2:
                        Unsafe.Add(ref dRef, 1) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 1),
                                                                          Unsafe.Add(ref yRef, 1),
                                                                          Unsafe.Add(ref zRef, 1));
                        goto case 1;

                    case 1:
                        dRef = TTernaryOperator.Invoke(xRef, yRef, zRef);
                        goto case 0;

                    case 0:
                        break;
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void VectorizedSmall4(ref T xRef, ref T yRef, ref T zRef, ref T dRef, nuint remainder)
            {
                Debug.Assert(sizeof(T) == 4);

                switch (remainder)
                {
                    case 15:
                    case 14:
                    case 13:
                    case 12:
                    case 11:
                    case 10:
                    case 9:
                        {
                            Debug.Assert(Vector256.IsHardwareAccelerated);

                            Vector256<T> beg = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef),
                                                                       Vector256.LoadUnsafe(ref yRef),
                                                                       Vector256.LoadUnsafe(ref zRef));
                            Vector256<T> end = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)Vector256<T>.Count),
                                                                       Vector256.LoadUnsafe(ref yRef, remainder - (uint)Vector256<T>.Count),
                                                                       Vector256.LoadUnsafe(ref zRef, remainder - (uint)Vector256<T>.Count));

                            beg.StoreUnsafe(ref dRef);
                            end.StoreUnsafe(ref dRef, remainder - (uint)Vector256<T>.Count);

                            break;
                        }

                    case 8:
                        {
                            Debug.Assert(Vector256.IsHardwareAccelerated);

                            Vector256<T> beg = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef),
                                                                           Vector256.LoadUnsafe(ref yRef),
                                                                           Vector256.LoadUnsafe(ref zRef));
                            beg.StoreUnsafe(ref dRef);

                            break;
                        }

                    case 7:
                    case 6:
                    case 5:
                        {
                            Debug.Assert(Vector128.IsHardwareAccelerated);

                            Vector128<T> beg = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef),
                                                                           Vector128.LoadUnsafe(ref yRef),
                                                                           Vector128.LoadUnsafe(ref zRef));
                            Vector128<T> end = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)Vector128<T>.Count),
                                                                           Vector128.LoadUnsafe(ref yRef, remainder - (uint)Vector128<T>.Count),
                                                                           Vector128.LoadUnsafe(ref zRef, remainder - (uint)Vector128<T>.Count));

                            beg.StoreUnsafe(ref dRef);
                            end.StoreUnsafe(ref dRef, remainder - (uint)Vector128<T>.Count);

                            break;
                        }

                    case 4:
                        {
                            Debug.Assert(Vector128.IsHardwareAccelerated);

                            Vector128<T> beg = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef),
                                                                           Vector128.LoadUnsafe(ref yRef),
                                                                           Vector128.LoadUnsafe(ref zRef));
                            beg.StoreUnsafe(ref dRef);

                            break;
                        }

                    case 3:
                        {
                            Unsafe.Add(ref dRef, 2) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 2),
                                                                              Unsafe.Add(ref yRef, 2),
                                                                              Unsafe.Add(ref zRef, 2));
                            goto case 2;
                        }

                    case 2:
                        {
                            Unsafe.Add(ref dRef, 1) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 1),
                                                                              Unsafe.Add(ref yRef, 1),
                                                                              Unsafe.Add(ref zRef, 1));
                            goto case 1;
                        }

                    case 1:
                        {
                            dRef = TTernaryOperator.Invoke(xRef, yRef, zRef);
                            goto case 0;
                        }

                    case 0:
                        {
                            break;
                        }
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void VectorizedSmall8(ref T xRef, ref T yRef, ref T zRef, ref T dRef, nuint remainder)
            {
                Debug.Assert(sizeof(T) == 8);

                switch (remainder)
                {
                    case 7:
                    case 6:
                    case 5:
                        {
                            Debug.Assert(Vector256.IsHardwareAccelerated);

                            Vector256<T> beg = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef),
                                                                       Vector256.LoadUnsafe(ref yRef),
                                                                       Vector256.LoadUnsafe(ref zRef));
                            Vector256<T> end = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)Vector256<T>.Count),
                                                                       Vector256.LoadUnsafe(ref yRef, remainder - (uint)Vector256<T>.Count),
                                                                       Vector256.LoadUnsafe(ref zRef, remainder - (uint)Vector256<T>.Count));

                            beg.StoreUnsafe(ref dRef);
                            end.StoreUnsafe(ref dRef, remainder - (uint)Vector256<T>.Count);

                            break;
                        }

                    case 4:
                        {
                            Debug.Assert(Vector256.IsHardwareAccelerated);

                            Vector256<T> beg = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef),
                                                                       Vector256.LoadUnsafe(ref yRef),
                                                                       Vector256.LoadUnsafe(ref zRef));
                            beg.StoreUnsafe(ref dRef);

                            break;
                        }

                    case 3:
                        {
                            Debug.Assert(Vector128.IsHardwareAccelerated);

                            Vector128<T> beg = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef),
                                                                       Vector128.LoadUnsafe(ref yRef),
                                                                       Vector128.LoadUnsafe(ref zRef));
                            Vector128<T> end = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)Vector128<T>.Count),
                                                                       Vector128.LoadUnsafe(ref yRef, remainder - (uint)Vector128<T>.Count),
                                                                       Vector128.LoadUnsafe(ref zRef, remainder - (uint)Vector128<T>.Count));

                            beg.StoreUnsafe(ref dRef);
                            end.StoreUnsafe(ref dRef, remainder - (uint)Vector128<T>.Count);

                            break;
                        }

                    case 2:
                        {
                            Debug.Assert(Vector128.IsHardwareAccelerated);

                            Vector128<T> beg = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef),
                                                                       Vector128.LoadUnsafe(ref yRef),
                                                                       Vector128.LoadUnsafe(ref zRef));
                            beg.StoreUnsafe(ref dRef);

                            break;
                        }

                    case 1:
                        {
                            dRef = TTernaryOperator.Invoke(xRef, yRef, zRef);
                            goto case 0;
                        }

                    case 0:
                        {
                            break;
                        }
                }
            }
        }

        /// <summary>
        /// Performs an element-wise operation on <paramref name="x"/>, <paramref name="y"/>, and <paramref name="z"/>,
        /// and writes the results to <paramref name="destination"/>.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <typeparam name="TTernaryOperator">
        /// Specifies the operation to perform on the pair-wise elements loaded from <paramref name="x"/> and <paramref name="y"/>
        /// with <paramref name="z"/>.
        /// </typeparam>
        private static void InvokeSpanSpanScalarIntoSpan<T, TTernaryOperator>(
            ReadOnlySpan<T> x, ReadOnlySpan<T> y, T z, Span<T> destination)
            where TTernaryOperator : struct, ITernaryOperator<T>
        {
            if (x.Length != y.Length)
            {
                ThrowHelper.ThrowArgument_SpansMustHaveSameLength();
            }

            if (x.Length > destination.Length)
            {
                ThrowHelper.ThrowArgument_DestinationTooShort();
            }

            ValidateInputOutputSpanNonOverlapping(x, destination);
            ValidateInputOutputSpanNonOverlapping(y, destination);

            // Since every branch has a cost and since that cost is
            // essentially lost for larger inputs, we do branches
            // in a way that allows us to have the minimum possible
            // for small sizes

            ref T xRef = ref MemoryMarshal.GetReference(x);
            ref T yRef = ref MemoryMarshal.GetReference(y);
            ref T dRef = ref MemoryMarshal.GetReference(destination);

            nuint remainder = (uint)x.Length;

            if (TTernaryOperator.Vectorizable && Vector512.IsHardwareAccelerated && Vector512<T>.IsSupported)
            {
                if (remainder >= (uint)Vector512<T>.Count)
                {
                    Vectorized512(ref xRef, ref yRef, z, ref dRef, remainder);
                }
                else
                {
                    // We have less than a vector and so we can only handle this as scalar. To do this
                    // efficiently, we simply have a small jump table and fallthrough. So we get a simple
                    // length check, single jump, and then linear execution.

                    VectorizedSmall(ref xRef, ref yRef, z, ref dRef, remainder);
                }

                return;
            }

            if (TTernaryOperator.Vectorizable && Vector256.IsHardwareAccelerated && Vector256<T>.IsSupported)
            {
                if (remainder >= (uint)Vector256<T>.Count)
                {
                    Vectorized256(ref xRef, ref yRef, z, ref dRef, remainder);
                }
                else
                {
                    // We have less than a vector and so we can only handle this as scalar. To do this
                    // efficiently, we simply have a small jump table and fallthrough. So we get a simple
                    // length check, single jump, and then linear execution.

                    VectorizedSmall(ref xRef, ref yRef, z, ref dRef, remainder);
                }

                return;
            }

            if (TTernaryOperator.Vectorizable && Vector128.IsHardwareAccelerated && Vector128<T>.IsSupported)
            {
                if (remainder >= (uint)Vector128<T>.Count)
                {
                    Vectorized128(ref xRef, ref yRef, z, ref dRef, remainder);
                }
                else
                {
                    // We have less than a vector and so we can only handle this as scalar. To do this
                    // efficiently, we simply have a small jump table and fallthrough. So we get a simple
                    // length check, single jump, and then linear execution.

                    VectorizedSmall(ref xRef, ref yRef, z, ref dRef, remainder);
                }

                return;
            }

            // This is the software fallback when no acceleration is available
            // It requires no branches to hit

            SoftwareFallback(ref xRef, ref yRef, z, ref dRef, remainder);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void SoftwareFallback(ref T xRef, ref T yRef, T z, ref T dRef, nuint length)
            {
                for (nuint i = 0; i < length; i++)
                {
                    Unsafe.Add(ref dRef, i) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, i),
                                                                      Unsafe.Add(ref yRef, i),
                                                                      z);
                }
            }

            static void Vectorized128(ref T xRef, ref T yRef, T z, ref T dRef, nuint remainder)
            {
                ref T dRefBeg = ref dRef;

                // Preload the beginning and end so that overlapping accesses don't negatively impact the data

                Vector128<T> zVec = Vector128.Create(z);

                Vector128<T> beg = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef),
                                                               Vector128.LoadUnsafe(ref yRef),
                                                               zVec);
                Vector128<T> end = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)Vector128<T>.Count),
                                                               Vector128.LoadUnsafe(ref yRef, remainder - (uint)Vector128<T>.Count),
                                                               zVec);

                if (remainder > (uint)(Vector128<T>.Count * 8))
                {
                    // Pinning is cheap and will be short lived for small inputs and unlikely to be impactful
                    // for large inputs (> 85KB) which are on the LOH and unlikely to be compacted.

                    fixed (T* px = &xRef)
                    fixed (T* py = &yRef)
                    fixed (T* pd = &dRef)
                    {
                        T* xPtr = px;
                        T* yPtr = py;
                        T* dPtr = pd;

                        // We need to the ensure the underlying data can be aligned and only align
                        // it if it can. It is possible we have an unaligned ref, in which case we
                        // can never achieve the required SIMD alignment.

                        bool canAlign = ((nuint)dPtr % (nuint)sizeof(T)) == 0;

                        if (canAlign)
                        {
                            // Compute by how many elements we're misaligned and adjust the pointers accordingly
                            //
                            // Noting that we are only actually aligning dPtr. This is because unaligned stores
                            // are more expensive than unaligned loads and aligning both is significantly more
                            // complex.

                            nuint misalignment = ((uint)sizeof(Vector128<T>) - ((nuint)dPtr % (uint)sizeof(Vector128<T>))) / (uint)sizeof(T);

                            xPtr += misalignment;
                            yPtr += misalignment;
                            dPtr += misalignment;

                            Debug.Assert(((nuint)dPtr % (uint)sizeof(Vector128<T>)) == 0);

                            remainder -= misalignment;
                        }

                        Vector128<T> vector1;
                        Vector128<T> vector2;
                        Vector128<T> vector3;
                        Vector128<T> vector4;

                        if ((remainder > (NonTemporalByteThreshold / (nuint)sizeof(T))) && canAlign)
                        {
                            // This loop stores the data non-temporally, which benefits us when there
                            // is a large amount of data involved as it avoids polluting the cache.

                            while (remainder >= (uint)(Vector128<T>.Count * 8))
                            {
                                // We load, process, and store the first four vectors

                                vector1 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 0)),
                                                                  Vector128.Load(yPtr + (uint)(Vector128<T>.Count * 0)),
                                                                  zVec);
                                vector2 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 1)),
                                                                  Vector128.Load(yPtr + (uint)(Vector128<T>.Count * 1)),
                                                                  zVec);
                                vector3 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 2)),
                                                                  Vector128.Load(yPtr + (uint)(Vector128<T>.Count * 2)),
                                                                  zVec);
                                vector4 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 3)),
                                                                  Vector128.Load(yPtr + (uint)(Vector128<T>.Count * 3)),
                                                                  zVec);

                                vector1.StoreAlignedNonTemporal(dPtr + (uint)(Vector128<T>.Count * 0));
                                vector2.StoreAlignedNonTemporal(dPtr + (uint)(Vector128<T>.Count * 1));
                                vector3.StoreAlignedNonTemporal(dPtr + (uint)(Vector128<T>.Count * 2));
                                vector4.StoreAlignedNonTemporal(dPtr + (uint)(Vector128<T>.Count * 3));

                                // We load, process, and store the next four vectors

                                vector1 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 4)),
                                                                  Vector128.Load(yPtr + (uint)(Vector128<T>.Count * 4)),
                                                                  zVec);
                                vector2 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 5)),
                                                                  Vector128.Load(yPtr + (uint)(Vector128<T>.Count * 5)),
                                                                  zVec);
                                vector3 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 6)),
                                                                  Vector128.Load(yPtr + (uint)(Vector128<T>.Count * 6)),
                                                                  zVec);
                                vector4 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 7)),
                                                                  Vector128.Load(yPtr + (uint)(Vector128<T>.Count * 7)),
                                                                  zVec);

                                vector1.StoreAlignedNonTemporal(dPtr + (uint)(Vector128<T>.Count * 4));
                                vector2.StoreAlignedNonTemporal(dPtr + (uint)(Vector128<T>.Count * 5));
                                vector3.StoreAlignedNonTemporal(dPtr + (uint)(Vector128<T>.Count * 6));
                                vector4.StoreAlignedNonTemporal(dPtr + (uint)(Vector128<T>.Count * 7));

                                // We adjust the source and destination references, then update
                                // the count of remaining elements to process.

                                xPtr += (uint)(Vector128<T>.Count * 8);
                                yPtr += (uint)(Vector128<T>.Count * 8);
                                dPtr += (uint)(Vector128<T>.Count * 8);

                                remainder -= (uint)(Vector128<T>.Count * 8);
                            }
                        }
                        else
                        {
                            while (remainder >= (uint)(Vector128<T>.Count * 8))
                            {
                                // We load, process, and store the first four vectors

                                vector1 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 0)),
                                                                  Vector128.Load(yPtr + (uint)(Vector128<T>.Count * 0)),
                                                                  zVec);
                                vector2 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 1)),
                                                                  Vector128.Load(yPtr + (uint)(Vector128<T>.Count * 1)),
                                                                  zVec);
                                vector3 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 2)),
                                                                  Vector128.Load(yPtr + (uint)(Vector128<T>.Count * 2)),
                                                                  zVec);
                                vector4 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 3)),
                                                                  Vector128.Load(yPtr + (uint)(Vector128<T>.Count * 3)),
                                                                  zVec);

                                vector1.Store(dPtr + (uint)(Vector128<T>.Count * 0));
                                vector2.Store(dPtr + (uint)(Vector128<T>.Count * 1));
                                vector3.Store(dPtr + (uint)(Vector128<T>.Count * 2));
                                vector4.Store(dPtr + (uint)(Vector128<T>.Count * 3));

                                // We load, process, and store the next four vectors

                                vector1 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 4)),
                                                                  Vector128.Load(yPtr + (uint)(Vector128<T>.Count * 4)),
                                                                  zVec);
                                vector2 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 5)),
                                                                  Vector128.Load(yPtr + (uint)(Vector128<T>.Count * 5)),
                                                                  zVec);
                                vector3 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 6)),
                                                                  Vector128.Load(yPtr + (uint)(Vector128<T>.Count * 6)),
                                                                  zVec);
                                vector4 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 7)),
                                                                  Vector128.Load(yPtr + (uint)(Vector128<T>.Count * 7)),
                                                                  zVec);

                                vector1.Store(dPtr + (uint)(Vector128<T>.Count * 4));
                                vector2.Store(dPtr + (uint)(Vector128<T>.Count * 5));
                                vector3.Store(dPtr + (uint)(Vector128<T>.Count * 6));
                                vector4.Store(dPtr + (uint)(Vector128<T>.Count * 7));

                                // We adjust the source and destination references, then update
                                // the count of remaining elements to process.

                                xPtr += (uint)(Vector128<T>.Count * 8);
                                yPtr += (uint)(Vector128<T>.Count * 8);
                                dPtr += (uint)(Vector128<T>.Count * 8);

                                remainder -= (uint)(Vector128<T>.Count * 8);
                            }
                        }

                        // Adjusting the refs here allows us to avoid pinning for very small inputs

                        xRef = ref *xPtr;
                        yRef = ref *yPtr;
                        dRef = ref *dPtr;
                    }
                }

                // Process the remaining [Count, Count * 8] elements via a jump table
                //
                // Unless the original length was an exact multiple of Count, then we'll
                // end up reprocessing a couple elements in case 1 for end. We'll also
                // potentially reprocess a few elements in case 0 for beg, to handle any
                // data before the first aligned address.

                nuint endIndex = remainder;
                remainder = (remainder + (uint)(Vector128<T>.Count - 1)) & (nuint)(-Vector128<T>.Count);

                switch (remainder / (uint)Vector128<T>.Count)
                {
                    case 8:
                        {
                            Vector128<T> vector = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)(Vector128<T>.Count * 8)),
                                                                          Vector128.LoadUnsafe(ref yRef, remainder - (uint)(Vector128<T>.Count * 8)),
                                                                          zVec);
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector128<T>.Count * 8));
                            goto case 7;
                        }

                    case 7:
                        {
                            Vector128<T> vector = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)(Vector128<T>.Count * 7)),
                                                                          Vector128.LoadUnsafe(ref yRef, remainder - (uint)(Vector128<T>.Count * 7)),
                                                                          zVec);
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector128<T>.Count * 7));
                            goto case 6;
                        }

                    case 6:
                        {
                            Vector128<T> vector = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)(Vector128<T>.Count * 6)),
                                                                          Vector128.LoadUnsafe(ref yRef, remainder - (uint)(Vector128<T>.Count * 6)),
                                                                          zVec);
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector128<T>.Count * 6));
                            goto case 5;
                        }

                    case 5:
                        {
                            Vector128<T> vector = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)(Vector128<T>.Count * 5)),
                                                                          Vector128.LoadUnsafe(ref yRef, remainder - (uint)(Vector128<T>.Count * 5)),
                                                                          zVec);
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector128<T>.Count * 5));
                            goto case 4;
                        }

                    case 4:
                        {
                            Vector128<T> vector = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)(Vector128<T>.Count * 4)),
                                                                          Vector128.LoadUnsafe(ref yRef, remainder - (uint)(Vector128<T>.Count * 4)),
                                                                          zVec);
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector128<T>.Count * 4));
                            goto case 3;
                        }

                    case 3:
                        {
                            Vector128<T> vector = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)(Vector128<T>.Count * 3)),
                                                                          Vector128.LoadUnsafe(ref yRef, remainder - (uint)(Vector128<T>.Count * 3)),
                                                                          zVec);
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector128<T>.Count * 3));
                            goto case 2;
                        }

                    case 2:
                        {
                            Vector128<T> vector = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)(Vector128<T>.Count * 2)),
                                                                          Vector128.LoadUnsafe(ref yRef, remainder - (uint)(Vector128<T>.Count * 2)),
                                                                          zVec);
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector128<T>.Count * 2));
                            goto case 1;
                        }

                    case 1:
                        {
                            // Store the last block, which includes any elements that wouldn't fill a full vector
                            end.StoreUnsafe(ref dRef, endIndex - (uint)Vector128<T>.Count);
                            goto case 0;
                        }

                    case 0:
                        {
                            // Store the first block, which includes any elements preceding the first aligned block
                            beg.StoreUnsafe(ref dRefBeg);
                            break;
                        }
                }
            }

            static void Vectorized256(ref T xRef, ref T yRef, T z, ref T dRef, nuint remainder)
            {
                ref T dRefBeg = ref dRef;

                // Preload the beginning and end so that overlapping accesses don't negatively impact the data

                Vector256<T> zVec = Vector256.Create(z);

                Vector256<T> beg = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef),
                                                               Vector256.LoadUnsafe(ref yRef),
                                                               zVec);
                Vector256<T> end = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)Vector256<T>.Count),
                                                               Vector256.LoadUnsafe(ref yRef, remainder - (uint)Vector256<T>.Count),
                                                               zVec);

                if (remainder > (uint)(Vector256<T>.Count * 8))
                {
                    // Pinning is cheap and will be short lived for small inputs and unlikely to be impactful
                    // for large inputs (> 85KB) which are on the LOH and unlikely to be compacted.

                    fixed (T* px = &xRef)
                    fixed (T* py = &yRef)
                    fixed (T* pd = &dRef)
                    {
                        T* xPtr = px;
                        T* yPtr = py;
                        T* dPtr = pd;

                        // We need to the ensure the underlying data can be aligned and only align
                        // it if it can. It is possible we have an unaligned ref, in which case we
                        // can never achieve the required SIMD alignment.

                        bool canAlign = ((nuint)dPtr % (nuint)sizeof(T)) == 0;

                        if (canAlign)
                        {
                            // Compute by how many elements we're misaligned and adjust the pointers accordingly
                            //
                            // Noting that we are only actually aligning dPtr. This is because unaligned stores
                            // are more expensive than unaligned loads and aligning both is significantly more
                            // complex.

                            nuint misalignment = ((uint)sizeof(Vector256<T>) - ((nuint)dPtr % (uint)sizeof(Vector256<T>))) / (uint)sizeof(T);

                            xPtr += misalignment;
                            yPtr += misalignment;
                            dPtr += misalignment;

                            Debug.Assert(((nuint)dPtr % (uint)sizeof(Vector256<T>)) == 0);

                            remainder -= misalignment;
                        }

                        Vector256<T> vector1;
                        Vector256<T> vector2;
                        Vector256<T> vector3;
                        Vector256<T> vector4;

                        if ((remainder > (NonTemporalByteThreshold / (nuint)sizeof(T))) && canAlign)
                        {
                            // This loop stores the data non-temporally, which benefits us when there
                            // is a large amount of data involved as it avoids polluting the cache.

                            while (remainder >= (uint)(Vector256<T>.Count * 8))
                            {
                                // We load, process, and store the first four vectors

                                vector1 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 0)),
                                                                  Vector256.Load(yPtr + (uint)(Vector256<T>.Count * 0)),
                                                                  zVec);
                                vector2 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 1)),
                                                                  Vector256.Load(yPtr + (uint)(Vector256<T>.Count * 1)),
                                                                  zVec);
                                vector3 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 2)),
                                                                  Vector256.Load(yPtr + (uint)(Vector256<T>.Count * 2)),
                                                                  zVec);
                                vector4 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 3)),
                                                                  Vector256.Load(yPtr + (uint)(Vector256<T>.Count * 3)),
                                                                  zVec);

                                vector1.StoreAlignedNonTemporal(dPtr + (uint)(Vector256<T>.Count * 0));
                                vector2.StoreAlignedNonTemporal(dPtr + (uint)(Vector256<T>.Count * 1));
                                vector3.StoreAlignedNonTemporal(dPtr + (uint)(Vector256<T>.Count * 2));
                                vector4.StoreAlignedNonTemporal(dPtr + (uint)(Vector256<T>.Count * 3));

                                // We load, process, and store the next four vectors

                                vector1 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 4)),
                                                                  Vector256.Load(yPtr + (uint)(Vector256<T>.Count * 4)),
                                                                  zVec);
                                vector2 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 5)),
                                                                  Vector256.Load(yPtr + (uint)(Vector256<T>.Count * 5)),
                                                                  zVec);
                                vector3 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 6)),
                                                                  Vector256.Load(yPtr + (uint)(Vector256<T>.Count * 6)),
                                                                  zVec);
                                vector4 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 7)),
                                                                  Vector256.Load(yPtr + (uint)(Vector256<T>.Count * 7)),
                                                                  zVec);

                                vector1.StoreAlignedNonTemporal(dPtr + (uint)(Vector256<T>.Count * 4));
                                vector2.StoreAlignedNonTemporal(dPtr + (uint)(Vector256<T>.Count * 5));
                                vector3.StoreAlignedNonTemporal(dPtr + (uint)(Vector256<T>.Count * 6));
                                vector4.StoreAlignedNonTemporal(dPtr + (uint)(Vector256<T>.Count * 7));

                                // We adjust the source and destination references, then update
                                // the count of remaining elements to process.

                                xPtr += (uint)(Vector256<T>.Count * 8);
                                yPtr += (uint)(Vector256<T>.Count * 8);
                                dPtr += (uint)(Vector256<T>.Count * 8);

                                remainder -= (uint)(Vector256<T>.Count * 8);
                            }
                        }
                        else
                        {
                            while (remainder >= (uint)(Vector256<T>.Count * 8))
                            {
                                // We load, process, and store the first four vectors

                                vector1 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 0)),
                                                                  Vector256.Load(yPtr + (uint)(Vector256<T>.Count * 0)),
                                                                  zVec);
                                vector2 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 1)),
                                                                  Vector256.Load(yPtr + (uint)(Vector256<T>.Count * 1)),
                                                                  zVec);
                                vector3 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 2)),
                                                                  Vector256.Load(yPtr + (uint)(Vector256<T>.Count * 2)),
                                                                  zVec);
                                vector4 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 3)),
                                                                  Vector256.Load(yPtr + (uint)(Vector256<T>.Count * 3)),
                                                                  zVec);

                                vector1.Store(dPtr + (uint)(Vector256<T>.Count * 0));
                                vector2.Store(dPtr + (uint)(Vector256<T>.Count * 1));
                                vector3.Store(dPtr + (uint)(Vector256<T>.Count * 2));
                                vector4.Store(dPtr + (uint)(Vector256<T>.Count * 3));

                                // We load, process, and store the next four vectors

                                vector1 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 4)),
                                                                  Vector256.Load(yPtr + (uint)(Vector256<T>.Count * 4)),
                                                                  zVec);
                                vector2 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 5)),
                                                                  Vector256.Load(yPtr + (uint)(Vector256<T>.Count * 5)),
                                                                  zVec);
                                vector3 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 6)),
                                                                  Vector256.Load(yPtr + (uint)(Vector256<T>.Count * 6)),
                                                                  zVec);
                                vector4 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 7)),
                                                                  Vector256.Load(yPtr + (uint)(Vector256<T>.Count * 7)),
                                                                  zVec);

                                vector1.Store(dPtr + (uint)(Vector256<T>.Count * 4));
                                vector2.Store(dPtr + (uint)(Vector256<T>.Count * 5));
                                vector3.Store(dPtr + (uint)(Vector256<T>.Count * 6));
                                vector4.Store(dPtr + (uint)(Vector256<T>.Count * 7));

                                // We adjust the source and destination references, then update
                                // the count of remaining elements to process.

                                xPtr += (uint)(Vector256<T>.Count * 8);
                                yPtr += (uint)(Vector256<T>.Count * 8);
                                dPtr += (uint)(Vector256<T>.Count * 8);

                                remainder -= (uint)(Vector256<T>.Count * 8);
                            }
                        }

                        // Adjusting the refs here allows us to avoid pinning for very small inputs

                        xRef = ref *xPtr;
                        yRef = ref *yPtr;
                        dRef = ref *dPtr;
                    }
                }

                // Process the remaining [Count, Count * 8] elements via a jump table
                //
                // Unless the original length was an exact multiple of Count, then we'll
                // end up reprocessing a couple elements in case 1 for end. We'll also
                // potentially reprocess a few elements in case 0 for beg, to handle any
                // data before the first aligned address.

                nuint endIndex = remainder;
                remainder = (remainder + (uint)(Vector256<T>.Count - 1)) & (nuint)(-Vector256<T>.Count);

                switch (remainder / (uint)Vector256<T>.Count)
                {
                    case 8:
                        {
                            Vector256<T> vector = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)(Vector256<T>.Count * 8)),
                                                                          Vector256.LoadUnsafe(ref yRef, remainder - (uint)(Vector256<T>.Count * 8)),
                                                                          zVec);
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector256<T>.Count * 8));
                            goto case 7;
                        }

                    case 7:
                        {
                            Vector256<T> vector = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)(Vector256<T>.Count * 7)),
                                                                          Vector256.LoadUnsafe(ref yRef, remainder - (uint)(Vector256<T>.Count * 7)),
                                                                          zVec);
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector256<T>.Count * 7));
                            goto case 6;
                        }

                    case 6:
                        {
                            Vector256<T> vector = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)(Vector256<T>.Count * 6)),
                                                                          Vector256.LoadUnsafe(ref yRef, remainder - (uint)(Vector256<T>.Count * 6)),
                                                                          zVec);
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector256<T>.Count * 6));
                            goto case 5;
                        }

                    case 5:
                        {
                            Vector256<T> vector = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)(Vector256<T>.Count * 5)),
                                                                          Vector256.LoadUnsafe(ref yRef, remainder - (uint)(Vector256<T>.Count * 5)),
                                                                          zVec);
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector256<T>.Count * 5));
                            goto case 4;
                        }

                    case 4:
                        {
                            Vector256<T> vector = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)(Vector256<T>.Count * 4)),
                                                                          Vector256.LoadUnsafe(ref yRef, remainder - (uint)(Vector256<T>.Count * 4)),
                                                                          zVec);
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector256<T>.Count * 4));
                            goto case 3;
                        }

                    case 3:
                        {
                            Vector256<T> vector = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)(Vector256<T>.Count * 3)),
                                                                          Vector256.LoadUnsafe(ref yRef, remainder - (uint)(Vector256<T>.Count * 3)),
                                                                          zVec);
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector256<T>.Count * 3));
                            goto case 2;
                        }

                    case 2:
                        {
                            Vector256<T> vector = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)(Vector256<T>.Count * 2)),
                                                                          Vector256.LoadUnsafe(ref yRef, remainder - (uint)(Vector256<T>.Count * 2)),
                                                                          zVec);
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector256<T>.Count * 2));
                            goto case 1;
                        }

                    case 1:
                        {
                            // Store the last block, which includes any elements that wouldn't fill a full vector
                            end.StoreUnsafe(ref dRef, endIndex - (uint)Vector256<T>.Count);
                            goto case 0;
                        }

                    case 0:
                        {
                            // Store the first block, which includes any elements preceding the first aligned block
                            beg.StoreUnsafe(ref dRefBeg);
                            break;
                        }
                }
            }

            static void Vectorized512(ref T xRef, ref T yRef, T z, ref T dRef, nuint remainder)
            {
                ref T dRefBeg = ref dRef;

                // Preload the beginning and end so that overlapping accesses don't negatively impact the data

                Vector512<T> zVec = Vector512.Create(z);

                Vector512<T> beg = TTernaryOperator.Invoke(Vector512.LoadUnsafe(ref xRef),
                                                           Vector512.LoadUnsafe(ref yRef),
                                                           zVec);
                Vector512<T> end = TTernaryOperator.Invoke(Vector512.LoadUnsafe(ref xRef, remainder - (uint)Vector512<T>.Count),
                                                           Vector512.LoadUnsafe(ref yRef, remainder - (uint)Vector512<T>.Count),
                                                           zVec);

                if (remainder > (uint)(Vector512<T>.Count * 8))
                {
                    // Pinning is cheap and will be short lived for small inputs and unlikely to be impactful
                    // for large inputs (> 85KB) which are on the LOH and unlikely to be compacted.

                    fixed (T* px = &xRef)
                    fixed (T* py = &yRef)
                    fixed (T* pd = &dRef)
                    {
                        T* xPtr = px;
                        T* yPtr = py;
                        T* dPtr = pd;

                        // We need to the ensure the underlying data can be aligned and only align
                        // it if it can. It is possible we have an unaligned ref, in which case we
                        // can never achieve the required SIMD alignment.

                        bool canAlign = ((nuint)dPtr % (nuint)sizeof(T)) == 0;

                        if (canAlign)
                        {
                            // Compute by how many elements we're misaligned and adjust the pointers accordingly
                            //
                            // Noting that we are only actually aligning dPtr. This is because unaligned stores
                            // are more expensive than unaligned loads and aligning both is significantly more
                            // complex.

                            nuint misalignment = ((uint)sizeof(Vector512<T>) - ((nuint)dPtr % (uint)sizeof(Vector512<T>))) / (uint)sizeof(T);

                            xPtr += misalignment;
                            yPtr += misalignment;
                            dPtr += misalignment;

                            Debug.Assert(((nuint)dPtr % (uint)sizeof(Vector512<T>)) == 0);

                            remainder -= misalignment;
                        }

                        Vector512<T> vector1;
                        Vector512<T> vector2;
                        Vector512<T> vector3;
                        Vector512<T> vector4;

                        if ((remainder > (NonTemporalByteThreshold / (nuint)sizeof(T))) && canAlign)
                        {
                            // This loop stores the data non-temporally, which benefits us when there
                            // is a large amount of data involved as it avoids polluting the cache.

                            while (remainder >= (uint)(Vector512<T>.Count * 8))
                            {
                                // We load, process, and store the first four vectors

                                vector1 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 0)),
                                                                  Vector512.Load(yPtr + (uint)(Vector512<T>.Count * 0)),
                                                                  zVec);
                                vector2 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 1)),
                                                                  Vector512.Load(yPtr + (uint)(Vector512<T>.Count * 1)),
                                                                  zVec);
                                vector3 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 2)),
                                                                  Vector512.Load(yPtr + (uint)(Vector512<T>.Count * 2)),
                                                                  zVec);
                                vector4 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 3)),
                                                                  Vector512.Load(yPtr + (uint)(Vector512<T>.Count * 3)),
                                                                  zVec);

                                vector1.StoreAlignedNonTemporal(dPtr + (uint)(Vector512<T>.Count * 0));
                                vector2.StoreAlignedNonTemporal(dPtr + (uint)(Vector512<T>.Count * 1));
                                vector3.StoreAlignedNonTemporal(dPtr + (uint)(Vector512<T>.Count * 2));
                                vector4.StoreAlignedNonTemporal(dPtr + (uint)(Vector512<T>.Count * 3));

                                // We load, process, and store the next four vectors

                                vector1 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 4)),
                                                                  Vector512.Load(yPtr + (uint)(Vector512<T>.Count * 4)),
                                                                  zVec);
                                vector2 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 5)),
                                                                  Vector512.Load(yPtr + (uint)(Vector512<T>.Count * 5)),
                                                                  zVec);
                                vector3 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 6)),
                                                                  Vector512.Load(yPtr + (uint)(Vector512<T>.Count * 6)),
                                                                  zVec);
                                vector4 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 7)),
                                                                  Vector512.Load(yPtr + (uint)(Vector512<T>.Count * 7)),
                                                                  zVec);

                                vector1.StoreAlignedNonTemporal(dPtr + (uint)(Vector512<T>.Count * 4));
                                vector2.StoreAlignedNonTemporal(dPtr + (uint)(Vector512<T>.Count * 5));
                                vector3.StoreAlignedNonTemporal(dPtr + (uint)(Vector512<T>.Count * 6));
                                vector4.StoreAlignedNonTemporal(dPtr + (uint)(Vector512<T>.Count * 7));

                                // We adjust the source and destination references, then update
                                // the count of remaining elements to process.

                                xPtr += (uint)(Vector512<T>.Count * 8);
                                yPtr += (uint)(Vector512<T>.Count * 8);
                                dPtr += (uint)(Vector512<T>.Count * 8);

                                remainder -= (uint)(Vector512<T>.Count * 8);
                            }
                        }
                        else
                        {
                            while (remainder >= (uint)(Vector512<T>.Count * 8))
                            {
                                // We load, process, and store the first four vectors

                                vector1 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 0)),
                                                                  Vector512.Load(yPtr + (uint)(Vector512<T>.Count * 0)),
                                                                  zVec);
                                vector2 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 1)),
                                                                  Vector512.Load(yPtr + (uint)(Vector512<T>.Count * 1)),
                                                                  zVec);
                                vector3 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 2)),
                                                                  Vector512.Load(yPtr + (uint)(Vector512<T>.Count * 2)),
                                                                  zVec);
                                vector4 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 3)),
                                                                  Vector512.Load(yPtr + (uint)(Vector512<T>.Count * 3)),
                                                                  zVec);

                                vector1.Store(dPtr + (uint)(Vector512<T>.Count * 0));
                                vector2.Store(dPtr + (uint)(Vector512<T>.Count * 1));
                                vector3.Store(dPtr + (uint)(Vector512<T>.Count * 2));
                                vector4.Store(dPtr + (uint)(Vector512<T>.Count * 3));

                                // We load, process, and store the next four vectors

                                vector1 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 4)),
                                                                  Vector512.Load(yPtr + (uint)(Vector512<T>.Count * 4)),
                                                                  zVec);
                                vector2 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 5)),
                                                                  Vector512.Load(yPtr + (uint)(Vector512<T>.Count * 5)),
                                                                  zVec);
                                vector3 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 6)),
                                                                  Vector512.Load(yPtr + (uint)(Vector512<T>.Count * 6)),
                                                                  zVec);
                                vector4 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 7)),
                                                                  Vector512.Load(yPtr + (uint)(Vector512<T>.Count * 7)),
                                                                  zVec);

                                vector1.Store(dPtr + (uint)(Vector512<T>.Count * 4));
                                vector2.Store(dPtr + (uint)(Vector512<T>.Count * 5));
                                vector3.Store(dPtr + (uint)(Vector512<T>.Count * 6));
                                vector4.Store(dPtr + (uint)(Vector512<T>.Count * 7));

                                // We adjust the source and destination references, then update
                                // the count of remaining elements to process.

                                xPtr += (uint)(Vector512<T>.Count * 8);
                                yPtr += (uint)(Vector512<T>.Count * 8);
                                dPtr += (uint)(Vector512<T>.Count * 8);

                                remainder -= (uint)(Vector512<T>.Count * 8);
                            }
                        }

                        // Adjusting the refs here allows us to avoid pinning for very small inputs

                        xRef = ref *xPtr;
                        yRef = ref *yPtr;
                        dRef = ref *dPtr;
                    }
                }

                // Process the remaining [Count, Count * 8] elements via a jump table
                //
                // Unless the original length was an exact multiple of Count, then we'll
                // end up reprocessing a couple elements in case 1 for end. We'll also
                // potentially reprocess a few elements in case 0 for beg, to handle any
                // data before the first aligned address.

                nuint endIndex = remainder;
                remainder = (remainder + (uint)(Vector512<T>.Count - 1)) & (nuint)(-Vector512<T>.Count);

                switch (remainder / (uint)Vector512<T>.Count)
                {
                    case 8:
                        {
                            Vector512<T> vector = TTernaryOperator.Invoke(Vector512.LoadUnsafe(ref xRef, remainder - (uint)(Vector512<T>.Count * 8)),
                                                                          Vector512.LoadUnsafe(ref yRef, remainder - (uint)(Vector512<T>.Count * 8)),
                                                                          zVec);
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector512<T>.Count * 8));
                            goto case 7;
                        }

                    case 7:
                        {
                            Vector512<T> vector = TTernaryOperator.Invoke(Vector512.LoadUnsafe(ref xRef, remainder - (uint)(Vector512<T>.Count * 7)),
                                                                          Vector512.LoadUnsafe(ref yRef, remainder - (uint)(Vector512<T>.Count * 7)),
                                                                          zVec);
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector512<T>.Count * 7));
                            goto case 6;
                        }

                    case 6:
                        {
                            Vector512<T> vector = TTernaryOperator.Invoke(Vector512.LoadUnsafe(ref xRef, remainder - (uint)(Vector512<T>.Count * 6)),
                                                                          Vector512.LoadUnsafe(ref yRef, remainder - (uint)(Vector512<T>.Count * 6)),
                                                                          zVec);
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector512<T>.Count * 6));
                            goto case 5;
                        }

                    case 5:
                        {
                            Vector512<T> vector = TTernaryOperator.Invoke(Vector512.LoadUnsafe(ref xRef, remainder - (uint)(Vector512<T>.Count * 5)),
                                                                          Vector512.LoadUnsafe(ref yRef, remainder - (uint)(Vector512<T>.Count * 5)),
                                                                          zVec);
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector512<T>.Count * 5));
                            goto case 4;
                        }

                    case 4:
                        {
                            Vector512<T> vector = TTernaryOperator.Invoke(Vector512.LoadUnsafe(ref xRef, remainder - (uint)(Vector512<T>.Count * 4)),
                                                                          Vector512.LoadUnsafe(ref yRef, remainder - (uint)(Vector512<T>.Count * 4)),
                                                                          zVec);
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector512<T>.Count * 4));
                            goto case 3;
                        }

                    case 3:
                        {
                            Vector512<T> vector = TTernaryOperator.Invoke(Vector512.LoadUnsafe(ref xRef, remainder - (uint)(Vector512<T>.Count * 3)),
                                                                          Vector512.LoadUnsafe(ref yRef, remainder - (uint)(Vector512<T>.Count * 3)),
                                                                          zVec);
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector512<T>.Count * 3));
                            goto case 2;
                        }

                    case 2:
                        {
                            Vector512<T> vector = TTernaryOperator.Invoke(Vector512.LoadUnsafe(ref xRef, remainder - (uint)(Vector512<T>.Count * 2)),
                                                                          Vector512.LoadUnsafe(ref yRef, remainder - (uint)(Vector512<T>.Count * 2)),
                                                                          zVec);
                            vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector512<T>.Count * 2));
                            goto case 1;
                        }

                    case 1:
                        {
                            // Store the last block, which includes any elements that wouldn't fill a full vector
                            end.StoreUnsafe(ref dRef, endIndex - (uint)Vector512<T>.Count);
                            goto case 0;
                        }

                    case 0:
                        {
                            // Store the first block, which includes any elements preceding the first aligned block
                            beg.StoreUnsafe(ref dRefBeg);
                            break;
                        }
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void VectorizedSmall(ref T xRef, ref T yRef, T z, ref T dRef, nuint remainder)
            {
                if (sizeof(T) == 1)
                {
                    VectorizedSmall1(ref xRef, ref yRef, z, ref dRef, remainder);
                }
                else if (sizeof(T) == 2)
                {
                    VectorizedSmall2(ref xRef, ref yRef, z, ref dRef, remainder);
                }
                else if (sizeof(T) == 4)
                {
                    VectorizedSmall4(ref xRef, ref yRef, z, ref dRef, remainder);
                }
                else
                {
                    Debug.Assert(sizeof(T) == 8);
                    VectorizedSmall8(ref xRef, ref yRef, z, ref dRef, remainder);
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void VectorizedSmall1(ref T xRef, ref T yRef, T z, ref T dRef, nuint remainder)
            {
                Debug.Assert(sizeof(T) == 1);

                switch (remainder)
                {
                    // Two Vector256's worth of data, with at least one element overlapping.
                    case 63:
                    case 62:
                    case 61:
                    case 60:
                    case 59:
                    case 58:
                    case 57:
                    case 56:
                    case 55:
                    case 54:
                    case 53:
                    case 52:
                    case 51:
                    case 50:
                    case 49:
                    case 48:
                    case 47:
                    case 46:
                    case 45:
                    case 44:
                    case 43:
                    case 42:
                    case 41:
                    case 40:
                    case 39:
                    case 38:
                    case 37:
                    case 36:
                    case 35:
                    case 34:
                    case 33:
                        {
                            Debug.Assert(Vector256.IsHardwareAccelerated);

                            Vector256<T> zVec = Vector256.Create(z);

                            Vector256<T> beg = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef),
                                                                       Vector256.LoadUnsafe(ref yRef),
                                                                       zVec);
                            Vector256<T> end = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)Vector256<T>.Count),
                                                                       Vector256.LoadUnsafe(ref yRef, remainder - (uint)Vector256<T>.Count),
                                                                       zVec);

                            beg.StoreUnsafe(ref dRef);
                            end.StoreUnsafe(ref dRef, remainder - (uint)Vector256<T>.Count);

                            break;
                        }

                    // One Vector256's worth of data.
                    case 32:
                        {
                            Debug.Assert(Vector256.IsHardwareAccelerated);

                            Vector256<T> beg = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef),
                                                                       Vector256.LoadUnsafe(ref yRef),
                                                                       Vector256.Create(z));
                            beg.StoreUnsafe(ref dRef);

                            break;
                        }

                    // Two Vector128's worth of data, with at least one element overlapping.
                    case 31:
                    case 30:
                    case 29:
                    case 28:
                    case 27:
                    case 26:
                    case 25:
                    case 24:
                    case 23:
                    case 22:
                    case 21:
                    case 20:
                    case 19:
                    case 18:
                    case 17:
                        {
                            Debug.Assert(Vector128.IsHardwareAccelerated);

                            Vector128<T> zVec = Vector128.Create(z);

                            Vector128<T> beg = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef),
                                                                       Vector128.LoadUnsafe(ref yRef),
                                                                       zVec);
                            Vector128<T> end = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)Vector128<T>.Count),
                                                                       Vector128.LoadUnsafe(ref yRef, remainder - (uint)Vector128<T>.Count),
                                                                       zVec);

                            beg.StoreUnsafe(ref dRef);
                            end.StoreUnsafe(ref dRef, remainder - (uint)Vector128<T>.Count);

                            break;
                        }

                    // One Vector128's worth of data.
                    case 16:
                        {
                            Debug.Assert(Vector128.IsHardwareAccelerated);

                            Vector128<T> beg = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef),
                                                                       Vector128.LoadUnsafe(ref yRef),
                                                                       Vector128.Create(z));
                            beg.StoreUnsafe(ref dRef);

                            break;
                        }

                    // Cases that are smaller than a single vector. No SIMD; just jump to the length and fall through each
                    // case to unroll the whole processing.
                    case 15:
                        Unsafe.Add(ref dRef, 14) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 14),
                                                                          Unsafe.Add(ref yRef, 14),
                                                                          z);
                        goto case 14;

                    case 14:
                        Unsafe.Add(ref dRef, 13) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 13),
                                                                          Unsafe.Add(ref yRef, 13),
                                                                          z);
                        goto case 13;

                    case 13:
                        Unsafe.Add(ref dRef, 12) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 12),
                                                                          Unsafe.Add(ref yRef, 12),
                                                                          z);
                        goto case 12;

                    case 12:
                        Unsafe.Add(ref dRef, 11) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 11),
                                                                          Unsafe.Add(ref yRef, 11),
                                                                          z);
                        goto case 11;

                    case 11:
                        Unsafe.Add(ref dRef, 10) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 10),
                                                                          Unsafe.Add(ref yRef, 10),
                                                                          z);
                        goto case 10;

                    case 10:
                        Unsafe.Add(ref dRef, 9) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 9),
                                                                          Unsafe.Add(ref yRef, 9),
                                                                          z);
                        goto case 9;

                    case 9:
                        Unsafe.Add(ref dRef, 8) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 8),
                                                                          Unsafe.Add(ref yRef, 8),
                                                                          z);
                        goto case 8;

                    case 8:
                        Unsafe.Add(ref dRef, 7) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 7),
                                                                          Unsafe.Add(ref yRef, 7),
                                                                          z);
                        goto case 7;

                    case 7:
                        Unsafe.Add(ref dRef, 6) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 6),
                                                                          Unsafe.Add(ref yRef, 6),
                                                                          z);
                        goto case 6;

                    case 6:
                        Unsafe.Add(ref dRef, 5) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 5),
                                                                          Unsafe.Add(ref yRef, 5),
                                                                          z);
                        goto case 5;

                    case 5:
                        Unsafe.Add(ref dRef, 4) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 4),
                                                                          Unsafe.Add(ref yRef, 4),
                                                                          z);
                        goto case 4;

                    case 4:
                        Unsafe.Add(ref dRef, 3) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 3),
                                                                          Unsafe.Add(ref yRef, 3),
                                                                          z);
                        goto case 3;

                    case 3:
                        Unsafe.Add(ref dRef, 2) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 2),
                                                                          Unsafe.Add(ref yRef, 2),
                                                                          z);
                        goto case 2;

                    case 2:
                        Unsafe.Add(ref dRef, 1) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 1),
                                                                          Unsafe.Add(ref yRef, 1),
                                                                          z);
                        goto case 1;

                    case 1:
                        dRef = TTernaryOperator.Invoke(xRef, yRef, z);
                        goto case 0;

                    case 0:
                        break;
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void VectorizedSmall2(ref T xRef, ref T yRef, T z, ref T dRef, nuint remainder)
            {
                Debug.Assert(sizeof(T) == 2);

                switch (remainder)
                {
                    // Two Vector256's worth of data, with at least one element overlapping.
                    case 31:
                    case 30:
                    case 29:
                    case 28:
                    case 27:
                    case 26:
                    case 25:
                    case 24:
                    case 23:
                    case 22:
                    case 21:
                    case 20:
                    case 19:
                    case 18:
                    case 17:
                        {
                            Debug.Assert(Vector256.IsHardwareAccelerated);

                            Vector256<T> zVec = Vector256.Create(z);

                            Vector256<T> beg = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef),
                                                                       Vector256.LoadUnsafe(ref yRef),
                                                                       zVec);
                            Vector256<T> end = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)Vector256<T>.Count),
                                                                       Vector256.LoadUnsafe(ref yRef, remainder - (uint)Vector256<T>.Count),
                                                                       zVec);

                            beg.StoreUnsafe(ref dRef);
                            end.StoreUnsafe(ref dRef, remainder - (uint)Vector256<T>.Count);

                            break;
                        }

                    // One Vector256's worth of data.
                    case 16:
                        {
                            Debug.Assert(Vector256.IsHardwareAccelerated);

                            Vector256<T> beg = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef),
                                                                       Vector256.LoadUnsafe(ref yRef),
                                                                       Vector256.Create(z));
                            beg.StoreUnsafe(ref dRef);

                            break;
                        }

                    // Two Vector128's worth of data, with at least one element overlapping.
                    case 15:
                    case 14:
                    case 13:
                    case 12:
                    case 11:
                    case 10:
                    case 9:
                        {
                            Debug.Assert(Vector128.IsHardwareAccelerated);

                            Vector128<T> zVec = Vector128.Create(z);

                            Vector128<T> beg = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef),
                                                                       Vector128.LoadUnsafe(ref yRef),
                                                                       zVec);
                            Vector128<T> end = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)Vector128<T>.Count),
                                                                       Vector128.LoadUnsafe(ref yRef, remainder - (uint)Vector128<T>.Count),
                                                                       zVec);

                            beg.StoreUnsafe(ref dRef);
                            end.StoreUnsafe(ref dRef, remainder - (uint)Vector128<T>.Count);

                            break;
                        }

                    // One Vector128's worth of data.
                    case 8:
                        {
                            Debug.Assert(Vector128.IsHardwareAccelerated);

                            Vector128<T> beg = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef),
                                                                       Vector128.LoadUnsafe(ref yRef),
                                                                       Vector128.Create(z));
                            beg.StoreUnsafe(ref dRef);

                            break;
                        }

                    // Cases that are smaller than a single vector. No SIMD; just jump to the length and fall through each
                    // case to unroll the whole processing.
                    case 7:
                        Unsafe.Add(ref dRef, 6) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 6),
                                                                          Unsafe.Add(ref yRef, 6),
                                                                          z);
                        goto case 6;

                    case 6:
                        Unsafe.Add(ref dRef, 5) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 5),
                                                                          Unsafe.Add(ref yRef, 5),
                                                                          z);
                        goto case 5;

                    case 5:
                        Unsafe.Add(ref dRef, 4) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 4),
                                                                          Unsafe.Add(ref yRef, 4),
                                                                          z);
                        goto case 4;

                    case 4:
                        Unsafe.Add(ref dRef, 3) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 3),
                                                                          Unsafe.Add(ref yRef, 3),
                                                                          z);
                        goto case 3;

                    case 3:
                        Unsafe.Add(ref dRef, 2) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 2),
                                                                          Unsafe.Add(ref yRef, 2),
                                                                          z);
                        goto case 2;

                    case 2:
                        Unsafe.Add(ref dRef, 1) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 1),
                                                                          Unsafe.Add(ref yRef, 1),
                                                                          z);
                        goto case 1;

                    case 1:
                        dRef = TTernaryOperator.Invoke(xRef, yRef, z);
                        goto case 0;

                    case 0:
                        break;
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void VectorizedSmall4(ref T xRef, ref T yRef, T z, ref T dRef, nuint remainder)
            {
                Debug.Assert(sizeof(T) == 4);

                switch (remainder)
                {
                    case 15:
                    case 14:
                    case 13:
                    case 12:
                    case 11:
                    case 10:
                    case 9:
                        {
                            Debug.Assert(Vector256.IsHardwareAccelerated);

                            Vector256<T> zVec = Vector256.Create(z);

                            Vector256<T> beg = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef),
                                                                       Vector256.LoadUnsafe(ref yRef),
                                                                       zVec);
                            Vector256<T> end = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)Vector256<T>.Count),
                                                                       Vector256.LoadUnsafe(ref yRef, remainder - (uint)Vector256<T>.Count),
                                                                       zVec);

                            beg.StoreUnsafe(ref dRef);
                            end.StoreUnsafe(ref dRef, remainder - (uint)Vector256<T>.Count);

                            break;
                        }

                    case 8:
                        {
                            Debug.Assert(Vector256.IsHardwareAccelerated);

                            Vector256<T> beg = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef),
                                                                       Vector256.LoadUnsafe(ref yRef),
                                                                       Vector256.Create(z));
                            beg.StoreUnsafe(ref dRef);

                            break;
                        }

                    case 7:
                    case 6:
                    case 5:
                        {
                            Debug.Assert(Vector128.IsHardwareAccelerated);

                            Vector128<T> zVec = Vector128.Create(z);

                            Vector128<T> beg = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef),
                                                                       Vector128.LoadUnsafe(ref yRef),
                                                                       zVec);
                            Vector128<T> end = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)Vector128<T>.Count),
                                                                       Vector128.LoadUnsafe(ref yRef, remainder - (uint)Vector128<T>.Count),
                                                                       zVec);

                            beg.StoreUnsafe(ref dRef);
                            end.StoreUnsafe(ref dRef, remainder - (uint)Vector128<T>.Count);

                            break;
                        }

                    case 4:
                        {
                            Debug.Assert(Vector128.IsHardwareAccelerated);

                            Vector128<T> beg = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef),
                                                                       Vector128.LoadUnsafe(ref yRef),
                                                                       Vector128.Create(z));
                            beg.StoreUnsafe(ref dRef);

                            break;
                        }

                    case 3:
                        {
                            Unsafe.Add(ref dRef, 2) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 2),
                                                                              Unsafe.Add(ref yRef, 2),
                                                                              z);
                            goto case 2;
                        }

                    case 2:
                        {
                            Unsafe.Add(ref dRef, 1) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 1),
                                                                              Unsafe.Add(ref yRef, 1),
                                                                              z);
                            goto case 1;
                        }

                    case 1:
                        {
                            dRef = TTernaryOperator.Invoke(xRef, yRef, z);
                            goto case 0;
                        }

                    case 0:
                        {
                            break;
                        }
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void VectorizedSmall8(ref T xRef, ref T yRef, T z, ref T dRef, nuint remainder)
            {
                Debug.Assert(sizeof(T) == 8);

                switch (remainder)
                {
                    case 7:
                    case 6:
                    case 5:
                        {
                            Debug.Assert(Vector256.IsHardwareAccelerated);

                            Vector256<T> zVec = Vector256.Create(z);

                            Vector256<T> beg = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef),
                                                                       Vector256.LoadUnsafe(ref yRef),
                                                                       zVec);
                            Vector256<T> end = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)Vector256<T>.Count),
                                                                       Vector256.LoadUnsafe(ref yRef, remainder - (uint)Vector256<T>.Count),
                                                                       zVec);

                            beg.StoreUnsafe(ref dRef);
                            end.StoreUnsafe(ref dRef, remainder - (uint)Vector256<T>.Count);

                            break;
                        }

                    case 4:
                        {
                            Debug.Assert(Vector256.IsHardwareAccelerated);

                            Vector256<T> beg = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef),
                                                                       Vector256.LoadUnsafe(ref yRef),
                                                                       Vector256.Create(z));
                            beg.StoreUnsafe(ref dRef);

                            break;
                        }

                    case 3:
                        {
                            Debug.Assert(Vector128.IsHardwareAccelerated);

                            Vector128<T> zVec = Vector128.Create(z);

                            Vector128<T> beg = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef),
                                                                       Vector128.LoadUnsafe(ref yRef),
                                                                       zVec);
                            Vector128<T> end = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)Vector128<T>.Count),
                                                                       Vector128.LoadUnsafe(ref yRef, remainder - (uint)Vector128<T>.Count),
                                                                       zVec);

                            beg.StoreUnsafe(ref dRef);
                            end.StoreUnsafe(ref dRef, remainder - (uint)Vector128<T>.Count);

                            break;
                        }

                    case 2:
                        {
                            Debug.Assert(Vector128.IsHardwareAccelerated);

                            Vector128<T> beg = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef),
                                                                       Vector128.LoadUnsafe(ref yRef),
                                                                       Vector128.Create(z));
                            beg.StoreUnsafe(ref dRef);

                            break;
                        }

                    case 1:
                        {
                            dRef = TTernaryOperator.Invoke(xRef, yRef, z);
                            goto case 0;
                        }

                    case 0:
                        {
                            break;
                        }
                }
            }
        }

        /// <summary>
        /// Performs an element-wise operation on <paramref name="x"/>, <paramref name="y"/>, and <paramref name="z"/>,
        /// and writes the results to <paramref name="destination"/>.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <typeparam name="TTernaryOperator">
        /// Specifies the operation to perform on the pair-wise element loaded from <paramref name="x"/>, with <paramref name="y"/>,
        /// and the element loaded from <paramref name="z"/>.
        /// </typeparam>
        private static void InvokeSpanScalarSpanIntoSpan<T, TTernaryOperator>(
            ReadOnlySpan<T> x, T y, ReadOnlySpan<T> z, Span<T> destination)
            where TTernaryOperator : struct, ITernaryOperator<T> =>
            InvokeSpanSpanScalarIntoSpan<T, SwappedYZTernaryOperator<TTernaryOperator, T>>(x, z, y, destination);

        /// <summary>
        /// Performs an element-wise operation on <paramref name="x"/>, <paramref name="y"/>, and <paramref name="z"/>,
        /// and writes the results to <paramref name="destination"/>.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <typeparam name="TTernaryOperator">
        /// Specifies the operation to perform on the pair-wise elements loaded from <paramref name="x"/> and <paramref name="y"/>
        /// with <paramref name="z"/>.
        /// </typeparam>
        private static void InvokeSpanScalarScalarIntoSpan<T, TTernaryOperator>(
            ReadOnlySpan<T> x, T y, T z, Span<T> destination)
            where TTernaryOperator : struct, ITernaryOperator<T>
        {
            if (x.Length > destination.Length)
            {
                ThrowHelper.ThrowArgument_DestinationTooShort();
            }

            ValidateInputOutputSpanNonOverlapping(x, destination);

            // Since every branch has a cost and since that cost is
            // essentially lost for larger inputs, we do branches
            // in a way that allows us to have the minimum possible
            // for small sizes

            ref T xRef = ref MemoryMarshal.GetReference(x);
            ref T dRef = ref MemoryMarshal.GetReference(destination);

            nuint remainder = (uint)x.Length;

            if (TTernaryOperator.Vectorizable && Vector512.IsHardwareAccelerated && Vector512<T>.IsSupported)
            {
                if (remainder >= (uint)Vector512<T>.Count)
                {
                    Vectorized512(ref xRef, y, z, ref dRef, remainder);
                }
                else
                {
                    // We have less than a vector and so we can only handle this as scalar. To do this
                    // efficiently, we simply have a small jump table and fallthrough. So we get a simple
                    // length check, single jump, and then linear execution.

                    VectorizedSmall(ref xRef, y, z, ref dRef, remainder);
                }

                return;
            }

            if (TTernaryOperator.Vectorizable && Vector256.IsHardwareAccelerated && Vector256<T>.IsSupported)
            {
                if (remainder >= (uint)Vector256<T>.Count)
                {
                    Vectorized256(ref xRef, y, z, ref dRef, remainder);
                }
                else
                {
                    // We have less than a vector and so we can only handle this as scalar. To do this
                    // efficiently, we simply have a small jump table and fallthrough. So we get a simple
                    // length check, single jump, and then linear execution.

                    VectorizedSmall(ref xRef, y, z, ref dRef, remainder);
                }

                return;
            }

            if (TTernaryOperator.Vectorizable && Vector128.IsHardwareAccelerated && Vector128<T>.IsSupported)
            {
                if (remainder >= (uint)Vector128<T>.Count)
                {
                    Vectorized128(ref xRef, y, z, ref dRef, remainder);
                }
                else
                {
                    // We have less than a vector and so we can only handle this as scalar. To do this
                    // efficiently, we simply have a small jump table and fallthrough. So we get a simple
                    // length check, single jump, and then linear execution.

                    VectorizedSmall(ref xRef, y, z, ref dRef, remainder);
                }

                return;
            }

            // This is the software fallback when no acceleration is available
            // It requires no branches to hit

            SoftwareFallback(ref xRef, y, z, ref dRef, remainder);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void SoftwareFallback(ref T xRef, T y, T z, ref T dRef, nuint length)
            {
                for (nuint i = 0; i < length; i++)
                {
                    Unsafe.Add(ref dRef, i) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, i),
                                                                      y,
                                                                      z);
                }
            }

            static void Vectorized128(ref T xRef, T y, T z, ref T dRef, nuint remainder)
            {
                ref T dRefBeg = ref dRef;

                // Preload the beginning and end so that overlapping accesses don't negatively impact the data

                Vector128<T> yVec = Vector128.Create(y);
                Vector128<T> zVec = Vector128.Create(z);

                Vector128<T> beg = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef),
                                                           yVec,
                                                           zVec);
                Vector128<T> end = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)Vector128<T>.Count),
                                                           yVec,
                                                           zVec);

                if (remainder > (uint)(Vector128<T>.Count * 8))
                {
                    // Pinning is cheap and will be short lived for small inputs and unlikely to be impactful
                    // for large inputs (> 85KB) which are on the LOH and unlikely to be compacted.

                    fixed (T* px = &xRef)
                    fixed (T* pd = &dRef)
                    {
                        T* xPtr = px;
                        T* dPtr = pd;

                        // We need to the ensure the underlying data can be aligned and only align
                        // it if it can. It is possible we have an unaligned ref, in which case we
                        // can never achieve the required SIMD alignment.

                        bool canAlign = ((nuint)dPtr % (nuint)sizeof(T)) == 0;

                        if (canAlign)
                        {
                            // Compute by how many elements we're misaligned and adjust the pointers accordingly
                            //
                            // Noting that we are only actually aligning dPtr. This is because unaligned stores
                            // are more expensive than unaligned loads and aligning both is significantly more
                            // complex.

                            nuint misalignment = ((uint)sizeof(Vector128<T>) - ((nuint)dPtr % (uint)sizeof(Vector128<T>))) / (uint)sizeof(T);

                            xPtr += misalignment;
                            dPtr += misalignment;

                            Debug.Assert(((nuint)dPtr % (uint)sizeof(Vector128<T>)) == 0);

                            remainder -= misalignment;
                        }

                        Vector128<T> vector1;
                        Vector128<T> vector2;
                        Vector128<T> vector3;
                        Vector128<T> vector4;

                        if ((remainder > (NonTemporalByteThreshold / (nuint)sizeof(T))) && canAlign)
                        {
                            // This loop stores the data non-temporally, which benefits us when there
                            // is a large amount of data involved as it avoids polluting the cache.

                            while (remainder >= (uint)(Vector128<T>.Count * 8))
                            {
                                // We load, process, and store the first four vectors

                                vector1 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 0)),
                                                                  yVec,
                                                                  zVec);
                                vector2 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 1)),
                                                                  yVec,
                                                                  zVec);
                                vector3 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 2)),
                                                                  yVec,
                                                                  zVec);
                                vector4 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 3)),
                                                                  yVec,
                                                                  zVec);

                                vector1.StoreAlignedNonTemporal(dPtr + (uint)(Vector128<T>.Count * 0));
                                vector2.StoreAlignedNonTemporal(dPtr + (uint)(Vector128<T>.Count * 1));
                                vector3.StoreAlignedNonTemporal(dPtr + (uint)(Vector128<T>.Count * 2));
                                vector4.StoreAlignedNonTemporal(dPtr + (uint)(Vector128<T>.Count * 3));

                                // We load, process, and store the next four vectors

                                vector1 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 4)),
                                                                  yVec,
                                                                  zVec);
                                vector2 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 5)),
                                                                  yVec,
                                                                  zVec);
                                vector3 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 6)),
                                                                  yVec,
                                                                  zVec);
                                vector4 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 7)),
                                                                  yVec,
                                                                  zVec);

                                vector1.StoreAlignedNonTemporal(dPtr + (uint)(Vector128<T>.Count * 4));
                                vector2.StoreAlignedNonTemporal(dPtr + (uint)(Vector128<T>.Count * 5));
                                vector3.StoreAlignedNonTemporal(dPtr + (uint)(Vector128<T>.Count * 6));
                                vector4.StoreAlignedNonTemporal(dPtr + (uint)(Vector128<T>.Count * 7));

                                // We adjust the source and destination references, then update
                                // the count of remaining elements to process.

                                xPtr += (uint)(Vector128<T>.Count * 8);
                                dPtr += (uint)(Vector128<T>.Count * 8);

                                remainder -= (uint)(Vector128<T>.Count * 8);
                            }
                        }
                        else
                        {
                            while (remainder >= (uint)(Vector128<T>.Count * 8))
                            {
                                // We load, process, and store the first four vectors

                                vector1 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 0)),
                                                                  yVec,
                                                                  zVec);
                                vector2 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 1)),
                                                                  yVec,
                                                                  zVec);
                                vector3 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 2)),
                                                                  yVec,
                                                                  zVec);
                                vector4 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 3)),
                                                                  yVec,
                                                                  zVec);

                                vector1.Store(dPtr + (uint)(Vector128<T>.Count * 0));
                                vector2.Store(dPtr + (uint)(Vector128<T>.Count * 1));
                                vector3.Store(dPtr + (uint)(Vector128<T>.Count * 2));
                                vector4.Store(dPtr + (uint)(Vector128<T>.Count * 3));

                                // We load, process, and store the next four vectors

                                vector1 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 4)),
                                                                  yVec,
                                                                  zVec);
                                vector2 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 5)),
                                                                  yVec,
                                                                  zVec);
                                vector3 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 6)),
                                                                  yVec,
                                                                  zVec);
                                vector4 = TTernaryOperator.Invoke(Vector128.Load(xPtr + (uint)(Vector128<T>.Count * 7)),
                                                                  yVec,
                                                                  zVec);

                                vector1.Store(dPtr + (uint)(Vector128<T>.Count * 4));
                                vector2.Store(dPtr + (uint)(Vector128<T>.Count * 5));
                                vector3.Store(dPtr + (uint)(Vector128<T>.Count * 6));
                                vector4.Store(dPtr + (uint)(Vector128<T>.Count * 7));

                                // We adjust the source and destination references, then update
                                // the count of remaining elements to process.

                                xPtr += (uint)(Vector128<T>.Count * 8);
                                dPtr += (uint)(Vector128<T>.Count * 8);

                                remainder -= (uint)(Vector128<T>.Count * 8);
                            }
                        }

                        // Adjusting the refs here allows us to avoid pinning for very small inputs

                        xRef = ref *xPtr;
                        dRef = ref *dPtr;
                    }
                }

                // Process the remaining [Count, Count * 8] elements via a jump table
                //
                // Unless the original length was an exact multiple of Count, then we'll
                // end up reprocessing a couple elements in case 1 for end. We'll also
                // potentially reprocess a few elements in case 0 for beg, to handle any
                // data before the first aligned address.

                nuint endIndex = remainder;
                remainder = (remainder + (uint)(Vector128<T>.Count - 1)) & (nuint)(-Vector128<T>.Count);

                switch (remainder / (uint)Vector128<T>.Count)
                {
                    case 8:
                    {
                        Vector128<T> vector = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)(Vector128<T>.Count * 8)),
                                                                      yVec,
                                                                      zVec);
                        vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector128<T>.Count * 8));
                        goto case 7;
                    }

                    case 7:
                    {
                        Vector128<T> vector = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)(Vector128<T>.Count * 7)),
                                                                      yVec,
                                                                      zVec);
                        vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector128<T>.Count * 7));
                        goto case 6;
                    }

                    case 6:
                    {
                        Vector128<T> vector = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)(Vector128<T>.Count * 6)),
                                                                      yVec,
                                                                      zVec);
                        vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector128<T>.Count * 6));
                        goto case 5;
                    }

                    case 5:
                    {
                        Vector128<T> vector = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)(Vector128<T>.Count * 5)),
                                                                      yVec,
                                                                      zVec);
                        vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector128<T>.Count * 5));
                        goto case 4;
                    }

                    case 4:
                    {
                        Vector128<T> vector = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)(Vector128<T>.Count * 4)),
                                                                      yVec,
                                                                      zVec);
                        vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector128<T>.Count * 4));
                        goto case 3;
                    }

                    case 3:
                    {
                        Vector128<T> vector = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)(Vector128<T>.Count * 3)),
                                                                      yVec,
                                                                      zVec);
                        vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector128<T>.Count * 3));
                        goto case 2;
                    }

                    case 2:
                    {
                        Vector128<T> vector = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)(Vector128<T>.Count * 2)),
                                                                      yVec,
                                                                      zVec);
                        vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector128<T>.Count * 2));
                        goto case 1;
                    }

                    case 1:
                    {
                        // Store the last block, which includes any elements that wouldn't fill a full vector
                        end.StoreUnsafe(ref dRef, endIndex - (uint)Vector128<T>.Count);
                        goto case 0;
                    }

                    case 0:
                    {
                        // Store the first block, which includes any elements preceding the first aligned block
                        beg.StoreUnsafe(ref dRefBeg);
                        break;
                    }
                }
            }

            static void Vectorized256(ref T xRef, T y, T z, ref T dRef, nuint remainder)
            {
                ref T dRefBeg = ref dRef;

                // Preload the beginning and end so that overlapping accesses don't negatively impact the data

                Vector256<T> yVec = Vector256.Create(y);
                Vector256<T> zVec = Vector256.Create(z);

                Vector256<T> beg = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef),
                                                           yVec,
                                                           zVec);
                Vector256<T> end = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)Vector256<T>.Count),
                                                           yVec,
                                                           zVec);

                if (remainder > (uint)(Vector256<T>.Count * 8))
                {
                    // Pinning is cheap and will be short lived for small inputs and unlikely to be impactful
                    // for large inputs (> 85KB) which are on the LOH and unlikely to be compacted.

                    fixed (T* px = &xRef)
                    fixed (T* pd = &dRef)
                    {
                        T* xPtr = px;
                        T* dPtr = pd;

                        // We need to the ensure the underlying data can be aligned and only align
                        // it if it can. It is possible we have an unaligned ref, in which case we
                        // can never achieve the required SIMD alignment.

                        bool canAlign = ((nuint)dPtr % (nuint)sizeof(T)) == 0;

                        if (canAlign)
                        {
                            // Compute by how many elements we're misaligned and adjust the pointers accordingly
                            //
                            // Noting that we are only actually aligning dPtr. This is because unaligned stores
                            // are more expensive than unaligned loads and aligning both is significantly more
                            // complex.

                            nuint misalignment = ((uint)sizeof(Vector256<T>) - ((nuint)dPtr % (uint)sizeof(Vector256<T>))) / (uint)sizeof(T);

                            xPtr += misalignment;
                            dPtr += misalignment;

                            Debug.Assert(((nuint)dPtr % (uint)sizeof(Vector256<T>)) == 0);

                            remainder -= misalignment;
                        }

                        Vector256<T> vector1;
                        Vector256<T> vector2;
                        Vector256<T> vector3;
                        Vector256<T> vector4;

                        if ((remainder > (NonTemporalByteThreshold / (nuint)sizeof(T))) && canAlign)
                        {
                            // This loop stores the data non-temporally, which benefits us when there
                            // is a large amount of data involved as it avoids polluting the cache.

                            while (remainder >= (uint)(Vector256<T>.Count * 8))
                            {
                                // We load, process, and store the first four vectors

                                vector1 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 0)),
                                                                  yVec,
                                                                  zVec);
                                vector2 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 1)),
                                                                  yVec,
                                                                  zVec);
                                vector3 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 2)),
                                                                  yVec,
                                                                  zVec);
                                vector4 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 3)),
                                                                  yVec,
                                                                  zVec);

                                vector1.StoreAlignedNonTemporal(dPtr + (uint)(Vector256<T>.Count * 0));
                                vector2.StoreAlignedNonTemporal(dPtr + (uint)(Vector256<T>.Count * 1));
                                vector3.StoreAlignedNonTemporal(dPtr + (uint)(Vector256<T>.Count * 2));
                                vector4.StoreAlignedNonTemporal(dPtr + (uint)(Vector256<T>.Count * 3));

                                // We load, process, and store the next four vectors

                                vector1 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 4)),
                                                                  yVec,
                                                                  zVec);
                                vector2 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 5)),
                                                                  yVec,
                                                                  zVec);
                                vector3 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 6)),
                                                                  yVec,
                                                                  zVec);
                                vector4 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 7)),
                                                                  yVec,
                                                                  zVec);

                                vector1.StoreAlignedNonTemporal(dPtr + (uint)(Vector256<T>.Count * 4));
                                vector2.StoreAlignedNonTemporal(dPtr + (uint)(Vector256<T>.Count * 5));
                                vector3.StoreAlignedNonTemporal(dPtr + (uint)(Vector256<T>.Count * 6));
                                vector4.StoreAlignedNonTemporal(dPtr + (uint)(Vector256<T>.Count * 7));

                                // We adjust the source and destination references, then update
                                // the count of remaining elements to process.

                                xPtr += (uint)(Vector256<T>.Count * 8);
                                dPtr += (uint)(Vector256<T>.Count * 8);

                                remainder -= (uint)(Vector256<T>.Count * 8);
                            }
                        }
                        else
                        {
                            while (remainder >= (uint)(Vector256<T>.Count * 8))
                            {
                                // We load, process, and store the first four vectors

                                vector1 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 0)),
                                                                  yVec,
                                                                  zVec);
                                vector2 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 1)),
                                                                  yVec,
                                                                  zVec);
                                vector3 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 2)),
                                                                  yVec,
                                                                  zVec);
                                vector4 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 3)),
                                                                  yVec,
                                                                  zVec);

                                vector1.Store(dPtr + (uint)(Vector256<T>.Count * 0));
                                vector2.Store(dPtr + (uint)(Vector256<T>.Count * 1));
                                vector3.Store(dPtr + (uint)(Vector256<T>.Count * 2));
                                vector4.Store(dPtr + (uint)(Vector256<T>.Count * 3));

                                // We load, process, and store the next four vectors

                                vector1 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 4)),
                                                                  yVec,
                                                                  zVec);
                                vector2 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 5)),
                                                                  yVec,
                                                                  zVec);
                                vector3 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 6)),
                                                                  yVec,
                                                                  zVec);
                                vector4 = TTernaryOperator.Invoke(Vector256.Load(xPtr + (uint)(Vector256<T>.Count * 7)),
                                                                  yVec,
                                                                  zVec);

                                vector1.Store(dPtr + (uint)(Vector256<T>.Count * 4));
                                vector2.Store(dPtr + (uint)(Vector256<T>.Count * 5));
                                vector3.Store(dPtr + (uint)(Vector256<T>.Count * 6));
                                vector4.Store(dPtr + (uint)(Vector256<T>.Count * 7));

                                // We adjust the source and destination references, then update
                                // the count of remaining elements to process.

                                xPtr += (uint)(Vector256<T>.Count * 8);
                                dPtr += (uint)(Vector256<T>.Count * 8);

                                remainder -= (uint)(Vector256<T>.Count * 8);
                            }
                        }

                        // Adjusting the refs here allows us to avoid pinning for very small inputs

                        xRef = ref *xPtr;
                        dRef = ref *dPtr;
                    }
                }

                // Process the remaining [Count, Count * 8] elements via a jump table
                //
                // Unless the original length was an exact multiple of Count, then we'll
                // end up reprocessing a couple elements in case 1 for end. We'll also
                // potentially reprocess a few elements in case 0 for beg, to handle any
                // data before the first aligned address.

                nuint endIndex = remainder;
                remainder = (remainder + (uint)(Vector256<T>.Count - 1)) & (nuint)(-Vector256<T>.Count);

                switch (remainder / (uint)Vector256<T>.Count)
                {
                    case 8:
                    {
                        Vector256<T> vector = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)(Vector256<T>.Count * 8)),
                                                                      yVec,
                                                                      zVec);
                        vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector256<T>.Count * 8));
                        goto case 7;
                    }

                    case 7:
                    {
                        Vector256<T> vector = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)(Vector256<T>.Count * 7)),
                                                                      yVec,
                                                                      zVec);
                        vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector256<T>.Count * 7));
                        goto case 6;
                    }

                    case 6:
                    {
                        Vector256<T> vector = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)(Vector256<T>.Count * 6)),
                                                                      yVec,
                                                                      zVec);
                        vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector256<T>.Count * 6));
                        goto case 5;
                    }

                    case 5:
                    {
                        Vector256<T> vector = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)(Vector256<T>.Count * 5)),
                                                                      yVec,
                                                                      zVec);
                        vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector256<T>.Count * 5));
                        goto case 4;
                    }

                    case 4:
                    {
                        Vector256<T> vector = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)(Vector256<T>.Count * 4)),
                                                                      yVec,
                                                                      zVec);
                        vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector256<T>.Count * 4));
                        goto case 3;
                    }

                    case 3:
                    {
                        Vector256<T> vector = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)(Vector256<T>.Count * 3)),
                                                                      yVec,
                                                                      zVec);
                        vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector256<T>.Count * 3));
                        goto case 2;
                    }

                    case 2:
                    {
                        Vector256<T> vector = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)(Vector256<T>.Count * 2)),
                                                                      yVec,
                                                                      zVec);
                        vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector256<T>.Count * 2));
                        goto case 1;
                    }

                    case 1:
                    {
                        // Store the last block, which includes any elements that wouldn't fill a full vector
                        end.StoreUnsafe(ref dRef, endIndex - (uint)Vector256<T>.Count);
                        goto case 0;
                    }

                    case 0:
                    {
                        // Store the first block, which includes any elements preceding the first aligned block
                        beg.StoreUnsafe(ref dRefBeg);
                        break;
                    }
                }
            }

            static void Vectorized512(ref T xRef, T y, T z, ref T dRef, nuint remainder)
            {
                ref T dRefBeg = ref dRef;

                // Preload the beginning and end so that overlapping accesses don't negatively impact the data

                Vector512<T> yVec = Vector512.Create(y);
                Vector512<T> zVec = Vector512.Create(z);

                Vector512<T> beg = TTernaryOperator.Invoke(Vector512.LoadUnsafe(ref xRef),
                                                           yVec,
                                                           zVec);
                Vector512<T> end = TTernaryOperator.Invoke(Vector512.LoadUnsafe(ref xRef, remainder - (uint)Vector512<T>.Count),
                                                           yVec,
                                                           zVec);

                if (remainder > (uint)(Vector512<T>.Count * 8))
                {
                    // Pinning is cheap and will be short lived for small inputs and unlikely to be impactful
                    // for large inputs (> 85KB) which are on the LOH and unlikely to be compacted.

                    fixed (T* px = &xRef)
                    fixed (T* pd = &dRef)
                    {
                        T* xPtr = px;
                        T* dPtr = pd;

                        // We need to the ensure the underlying data can be aligned and only align
                        // it if it can. It is possible we have an unaligned ref, in which case we
                        // can never achieve the required SIMD alignment.

                        bool canAlign = ((nuint)dPtr % (nuint)sizeof(T)) == 0;

                        if (canAlign)
                        {
                            // Compute by how many elements we're misaligned and adjust the pointers accordingly
                            //
                            // Noting that we are only actually aligning dPtr. This is because unaligned stores
                            // are more expensive than unaligned loads and aligning both is significantly more
                            // complex.

                            nuint misalignment = ((uint)sizeof(Vector512<T>) - ((nuint)dPtr % (uint)sizeof(Vector512<T>))) / (uint)sizeof(T);

                            xPtr += misalignment;
                            dPtr += misalignment;

                            Debug.Assert(((nuint)dPtr % (uint)sizeof(Vector512<T>)) == 0);

                            remainder -= misalignment;
                        }

                        Vector512<T> vector1;
                        Vector512<T> vector2;
                        Vector512<T> vector3;
                        Vector512<T> vector4;

                        if ((remainder > (NonTemporalByteThreshold / (nuint)sizeof(T))) && canAlign)
                        {
                            // This loop stores the data non-temporally, which benefits us when there
                            // is a large amount of data involved as it avoids polluting the cache.

                            while (remainder >= (uint)(Vector512<T>.Count * 8))
                            {
                                // We load, process, and store the first four vectors

                                vector1 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 0)),
                                                                  yVec,
                                                                  zVec);
                                vector2 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 1)),
                                                                  yVec,
                                                                  zVec);
                                vector3 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 2)),
                                                                  yVec,
                                                                  zVec);
                                vector4 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 3)),
                                                                  yVec,
                                                                  zVec);

                                vector1.StoreAlignedNonTemporal(dPtr + (uint)(Vector512<T>.Count * 0));
                                vector2.StoreAlignedNonTemporal(dPtr + (uint)(Vector512<T>.Count * 1));
                                vector3.StoreAlignedNonTemporal(dPtr + (uint)(Vector512<T>.Count * 2));
                                vector4.StoreAlignedNonTemporal(dPtr + (uint)(Vector512<T>.Count * 3));

                                // We load, process, and store the next four vectors

                                vector1 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 4)),
                                                                  yVec,
                                                                  zVec);
                                vector2 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 5)),
                                                                  yVec,
                                                                  zVec);
                                vector3 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 6)),
                                                                  yVec,
                                                                  zVec);
                                vector4 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 7)),
                                                                  yVec,
                                                                  zVec);

                                vector1.StoreAlignedNonTemporal(dPtr + (uint)(Vector512<T>.Count * 4));
                                vector2.StoreAlignedNonTemporal(dPtr + (uint)(Vector512<T>.Count * 5));
                                vector3.StoreAlignedNonTemporal(dPtr + (uint)(Vector512<T>.Count * 6));
                                vector4.StoreAlignedNonTemporal(dPtr + (uint)(Vector512<T>.Count * 7));

                                // We adjust the source and destination references, then update
                                // the count of remaining elements to process.

                                xPtr += (uint)(Vector512<T>.Count * 8);
                                dPtr += (uint)(Vector512<T>.Count * 8);

                                remainder -= (uint)(Vector512<T>.Count * 8);
                            }
                        }
                        else
                        {
                            while (remainder >= (uint)(Vector512<T>.Count * 8))
                            {
                                // We load, process, and store the first four vectors

                                vector1 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 0)),
                                                                  yVec,
                                                                  zVec);
                                vector2 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 1)),
                                                                  yVec,
                                                                  zVec);
                                vector3 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 2)),
                                                                  yVec,
                                                                  zVec);
                                vector4 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 3)),
                                                                  yVec,
                                                                  zVec);

                                vector1.Store(dPtr + (uint)(Vector512<T>.Count * 0));
                                vector2.Store(dPtr + (uint)(Vector512<T>.Count * 1));
                                vector3.Store(dPtr + (uint)(Vector512<T>.Count * 2));
                                vector4.Store(dPtr + (uint)(Vector512<T>.Count * 3));

                                // We load, process, and store the next four vectors

                                vector1 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 4)),
                                                                  yVec,
                                                                  zVec);
                                vector2 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 5)),
                                                                  yVec,
                                                                  zVec);
                                vector3 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 6)),
                                                                  yVec,
                                                                  zVec);
                                vector4 = TTernaryOperator.Invoke(Vector512.Load(xPtr + (uint)(Vector512<T>.Count * 7)),
                                                                  yVec,
                                                                  zVec);

                                vector1.Store(dPtr + (uint)(Vector512<T>.Count * 4));
                                vector2.Store(dPtr + (uint)(Vector512<T>.Count * 5));
                                vector3.Store(dPtr + (uint)(Vector512<T>.Count * 6));
                                vector4.Store(dPtr + (uint)(Vector512<T>.Count * 7));

                                // We adjust the source and destination references, then update
                                // the count of remaining elements to process.

                                xPtr += (uint)(Vector512<T>.Count * 8);
                                dPtr += (uint)(Vector512<T>.Count * 8);

                                remainder -= (uint)(Vector512<T>.Count * 8);
                            }
                        }

                        // Adjusting the refs here allows us to avoid pinning for very small inputs

                        xRef = ref *xPtr;
                        dRef = ref *dPtr;
                    }
                }

                // Process the remaining [Count, Count * 8] elements via a jump table
                //
                // Unless the original length was an exact multiple of Count, then we'll
                // end up reprocessing a couple elements in case 1 for end. We'll also
                // potentially reprocess a few elements in case 0 for beg, to handle any
                // data before the first aligned address.

                nuint endIndex = remainder;
                remainder = (remainder + (uint)(Vector512<T>.Count - 1)) & (nuint)(-Vector512<T>.Count);

                switch (remainder / (uint)Vector512<T>.Count)
                {
                    case 8:
                    {
                        Vector512<T> vector = TTernaryOperator.Invoke(Vector512.LoadUnsafe(ref xRef, remainder - (uint)(Vector512<T>.Count * 8)),
                                                                      yVec,
                                                                      zVec);
                        vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector512<T>.Count * 8));
                        goto case 7;
                    }

                    case 7:
                    {
                        Vector512<T> vector = TTernaryOperator.Invoke(Vector512.LoadUnsafe(ref xRef, remainder - (uint)(Vector512<T>.Count * 7)),
                                                                      yVec,
                                                                      zVec);
                        vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector512<T>.Count * 7));
                        goto case 6;
                    }

                    case 6:
                    {
                        Vector512<T> vector = TTernaryOperator.Invoke(Vector512.LoadUnsafe(ref xRef, remainder - (uint)(Vector512<T>.Count * 6)),
                                                                      yVec,
                                                                      zVec);
                        vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector512<T>.Count * 6));
                        goto case 5;
                    }

                    case 5:
                    {
                        Vector512<T> vector = TTernaryOperator.Invoke(Vector512.LoadUnsafe(ref xRef, remainder - (uint)(Vector512<T>.Count * 5)),
                                                                      yVec,
                                                                      zVec);
                        vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector512<T>.Count * 5));
                        goto case 4;
                    }

                    case 4:
                    {
                        Vector512<T> vector = TTernaryOperator.Invoke(Vector512.LoadUnsafe(ref xRef, remainder - (uint)(Vector512<T>.Count * 4)),
                                                                      yVec,
                                                                      zVec);
                        vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector512<T>.Count * 4));
                        goto case 3;
                    }

                    case 3:
                    {
                        Vector512<T> vector = TTernaryOperator.Invoke(Vector512.LoadUnsafe(ref xRef, remainder - (uint)(Vector512<T>.Count * 3)),
                                                                      yVec,
                                                                      zVec);
                        vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector512<T>.Count * 3));
                        goto case 2;
                    }

                    case 2:
                    {
                        Vector512<T> vector = TTernaryOperator.Invoke(Vector512.LoadUnsafe(ref xRef, remainder - (uint)(Vector512<T>.Count * 2)),
                                                                      yVec,
                                                                      zVec);
                        vector.StoreUnsafe(ref dRef, remainder - (uint)(Vector512<T>.Count * 2));
                        goto case 1;
                    }

                    case 1:
                    {
                        // Store the last block, which includes any elements that wouldn't fill a full vector
                        end.StoreUnsafe(ref dRef, endIndex - (uint)Vector512<T>.Count);
                        goto case 0;
                    }

                    case 0:
                    {
                        // Store the first block, which includes any elements preceding the first aligned block
                        beg.StoreUnsafe(ref dRefBeg);
                        break;
                    }
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void VectorizedSmall(ref T xRef, T y, T z, ref T dRef, nuint remainder)
            {
                if (sizeof(T) == 1)
                {
                    VectorizedSmall1(ref xRef, y, z, ref dRef, remainder);
                }
                else if (sizeof(T) == 2)
                {
                    VectorizedSmall2(ref xRef, y, z, ref dRef, remainder);
                }
                else if (sizeof(T) == 4)
                {
                    VectorizedSmall4(ref xRef, y, z, ref dRef, remainder);
                }
                else
                {
                    Debug.Assert(sizeof(T) == 8);
                    VectorizedSmall8(ref xRef, y, z, ref dRef, remainder);
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void VectorizedSmall1(ref T xRef, T y, T z, ref T dRef, nuint remainder)
            {
                Debug.Assert(sizeof(T) == 1);

                switch (remainder)
                {
                    // Two Vector256's worth of data, with at least one element overlapping.
                    case 63:
                    case 62:
                    case 61:
                    case 60:
                    case 59:
                    case 58:
                    case 57:
                    case 56:
                    case 55:
                    case 54:
                    case 53:
                    case 52:
                    case 51:
                    case 50:
                    case 49:
                    case 48:
                    case 47:
                    case 46:
                    case 45:
                    case 44:
                    case 43:
                    case 42:
                    case 41:
                    case 40:
                    case 39:
                    case 38:
                    case 37:
                    case 36:
                    case 35:
                    case 34:
                    case 33:
                    {
                        Debug.Assert(Vector256.IsHardwareAccelerated);

                        Vector256<T> yVec = Vector256.Create(y);
                        Vector256<T> zVec = Vector256.Create(z);

                        Vector256<T> beg = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef),
                                                                   yVec,
                                                                   zVec);
                        Vector256<T> end = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)Vector256<T>.Count),
                                                                   yVec,
                                                                   zVec);

                        beg.StoreUnsafe(ref dRef);
                        end.StoreUnsafe(ref dRef, remainder - (uint)Vector256<T>.Count);

                        break;
                    }

                    // One Vector256's worth of data.
                    case 32:
                    {
                        Debug.Assert(Vector256.IsHardwareAccelerated);

                        Vector256<T> beg = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef),
                                                                   Vector256.Create(y),
                                                                   Vector256.Create(z));
                        beg.StoreUnsafe(ref dRef);

                        break;
                    }

                    // Two Vector128's worth of data, with at least one element overlapping.
                    case 31:
                    case 30:
                    case 29:
                    case 28:
                    case 27:
                    case 26:
                    case 25:
                    case 24:
                    case 23:
                    case 22:
                    case 21:
                    case 20:
                    case 19:
                    case 18:
                    case 17:
                    {
                        Debug.Assert(Vector128.IsHardwareAccelerated);

                        Vector128<T> yVec = Vector128.Create(y);
                        Vector128<T> zVec = Vector128.Create(z);

                        Vector128<T> beg = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef),
                                                                   yVec,
                                                                   zVec);
                        Vector128<T> end = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)Vector128<T>.Count),
                                                                   yVec,
                                                                   zVec);

                        beg.StoreUnsafe(ref dRef);
                        end.StoreUnsafe(ref dRef, remainder - (uint)Vector128<T>.Count);

                        break;
                    }

                    // One Vector128's worth of data.
                    case 16:
                    {
                        Debug.Assert(Vector128.IsHardwareAccelerated);

                        Vector128<T> beg = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef),
                                                                   Vector128.Create(y),
                                                                   Vector128.Create(z));
                        beg.StoreUnsafe(ref dRef);

                        break;
                    }

                    // Cases that are smaller than a single vector. No SIMD; just jump to the length and fall through each
                    // case to unroll the whole processing.
                    case 15:
                        Unsafe.Add(ref dRef, 14) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 14),
                                                                           y,
                                                                           z);
                        goto case 14;

                    case 14:
                        Unsafe.Add(ref dRef, 13) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 13),
                                                                           y,
                                                                           z);
                        goto case 13;

                    case 13:
                        Unsafe.Add(ref dRef, 12) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 12),
                                                                           y,
                                                                           z);
                        goto case 12;

                    case 12:
                        Unsafe.Add(ref dRef, 11) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 11),
                                                                           y,
                                                                           z);
                        goto case 11;

                    case 11:
                        Unsafe.Add(ref dRef, 10) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 10),
                                                                           y,
                                                                           z);
                        goto case 10;

                    case 10:
                        Unsafe.Add(ref dRef, 9) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 9),
                                                                          y,
                                                                          z);
                        goto case 9;

                    case 9:
                        Unsafe.Add(ref dRef, 8) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 8),
                                                                          y,
                                                                          z);
                        goto case 8;

                    case 8:
                        Unsafe.Add(ref dRef, 7) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 7),
                                                                          y,
                                                                          z);
                        goto case 7;

                    case 7:
                        Unsafe.Add(ref dRef, 6) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 6),
                                                                          y,
                                                                          z);
                        goto case 6;

                    case 6:
                        Unsafe.Add(ref dRef, 5) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 5),
                                                                          y,
                                                                          z);
                        goto case 5;

                    case 5:
                        Unsafe.Add(ref dRef, 4) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 4),
                                                                          y,
                                                                          z);
                        goto case 4;

                    case 4:
                        Unsafe.Add(ref dRef, 3) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 3),
                                                                          y,
                                                                          z);
                        goto case 3;

                    case 3:
                        Unsafe.Add(ref dRef, 2) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 2),
                                                                          y,
                                                                          z);
                        goto case 2;

                    case 2:
                        Unsafe.Add(ref dRef, 1) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 1),
                                                                          y,
                                                                          z);
                        goto case 1;

                    case 1:
                        dRef = TTernaryOperator.Invoke(xRef, y, z);
                        goto case 0;

                    case 0:
                        break;
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void VectorizedSmall2(ref T xRef, T y, T z, ref T dRef, nuint remainder)
            {
                Debug.Assert(sizeof(T) == 2);

                switch (remainder)
                {
                    // Two Vector256's worth of data, with at least one element overlapping.
                    case 31:
                    case 30:
                    case 29:
                    case 28:
                    case 27:
                    case 26:
                    case 25:
                    case 24:
                    case 23:
                    case 22:
                    case 21:
                    case 20:
                    case 19:
                    case 18:
                    case 17:
                    {
                        Debug.Assert(Vector256.IsHardwareAccelerated);

                        Vector256<T> yVec = Vector256.Create(y);
                        Vector256<T> zVec = Vector256.Create(z);

                        Vector256<T> beg = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef),
                                                                   yVec,
                                                                   zVec);
                        Vector256<T> end = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)Vector256<T>.Count),
                                                                   yVec,
                                                                   zVec);

                        beg.StoreUnsafe(ref dRef);
                        end.StoreUnsafe(ref dRef, remainder - (uint)Vector256<T>.Count);

                        break;
                    }

                    // One Vector256's worth of data.
                    case 16:
                    {
                        Debug.Assert(Vector256.IsHardwareAccelerated);

                        Vector256<T> beg = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef),
                                                                   Vector256.Create(y),
                                                                   Vector256.Create(z));
                        beg.StoreUnsafe(ref dRef);

                        break;
                    }

                    // Two Vector128's worth of data, with at least one element overlapping.
                    case 15:
                    case 14:
                    case 13:
                    case 12:
                    case 11:
                    case 10:
                    case 9:
                    {
                        Debug.Assert(Vector128.IsHardwareAccelerated);

                        Vector128<T> yVec = Vector128.Create(y);
                        Vector128<T> zVec = Vector128.Create(z);

                        Vector128<T> beg = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef),
                                                                   yVec,
                                                                   zVec);
                        Vector128<T> end = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)Vector128<T>.Count),
                                                                   yVec,
                                                                   zVec);

                        beg.StoreUnsafe(ref dRef);
                        end.StoreUnsafe(ref dRef, remainder - (uint)Vector128<T>.Count);

                        break;
                    }

                    // One Vector128's worth of data.
                    case 8:
                    {
                        Debug.Assert(Vector128.IsHardwareAccelerated);

                        Vector128<T> beg = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef),
                                                                   Vector128.Create(y),
                                                                   Vector128.Create(z));
                        beg.StoreUnsafe(ref dRef);

                        break;
                    }

                    // Cases that are smaller than a single vector. No SIMD; just jump to the length and fall through each
                    // case to unroll the whole processing.
                    case 7:
                        Unsafe.Add(ref dRef, 6) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 6),
                                                                          y,
                                                                          z);
                        goto case 6;

                    case 6:
                        Unsafe.Add(ref dRef, 5) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 5),
                                                                          y,
                                                                          z);
                        goto case 5;

                    case 5:
                        Unsafe.Add(ref dRef, 4) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 4),
                                                                          y,
                                                                          z);
                        goto case 4;

                    case 4:
                        Unsafe.Add(ref dRef, 3) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 3),
                                                                          y,
                                                                          z);
                        goto case 3;

                    case 3:
                        Unsafe.Add(ref dRef, 2) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 2),
                                                                          y,
                                                                          z);
                        goto case 2;

                    case 2:
                        Unsafe.Add(ref dRef, 1) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 1),
                                                                          y,
                                                                          z);
                        goto case 1;

                    case 1:
                        dRef = TTernaryOperator.Invoke(xRef, y, z);
                        goto case 0;

                    case 0:
                        break;
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void VectorizedSmall4(ref T xRef, T y, T z, ref T dRef, nuint remainder)
            {
                Debug.Assert(sizeof(T) == 4);

                switch (remainder)
                {
                    case 15:
                    case 14:
                    case 13:
                    case 12:
                    case 11:
                    case 10:
                    case 9:
                    {
                        Debug.Assert(Vector256.IsHardwareAccelerated);

                        Vector256<T> yVec = Vector256.Create(y);
                        Vector256<T> zVec = Vector256.Create(z);

                        Vector256<T> beg = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef),
                                                                   yVec,
                                                                   zVec);
                        Vector256<T> end = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)Vector256<T>.Count),
                                                                   yVec,
                                                                   zVec);

                        beg.StoreUnsafe(ref dRef);
                        end.StoreUnsafe(ref dRef, remainder - (uint)Vector256<T>.Count);

                        break;
                    }

                    case 8:
                    {
                        Debug.Assert(Vector256.IsHardwareAccelerated);

                        Vector256<T> beg = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef),
                                                                   Vector256.Create(y),
                                                                   Vector256.Create(z));
                        beg.StoreUnsafe(ref dRef);

                        break;
                    }

                    case 7:
                    case 6:
                    case 5:
                    {
                        Debug.Assert(Vector128.IsHardwareAccelerated);

                        Vector128<T> yVec = Vector128.Create(y);
                        Vector128<T> zVec = Vector128.Create(z);

                        Vector128<T> beg = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef),
                                                                   yVec,
                                                                   zVec);
                        Vector128<T> end = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)Vector128<T>.Count),
                                                                   yVec,
                                                                   zVec);

                        beg.StoreUnsafe(ref dRef);
                        end.StoreUnsafe(ref dRef, remainder - (uint)Vector128<T>.Count);

                        break;
                    }

                    case 4:
                    {
                        Debug.Assert(Vector128.IsHardwareAccelerated);

                        Vector128<T> beg = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef),
                                                                   Vector128.Create(y),
                                                                   Vector128.Create(z));
                        beg.StoreUnsafe(ref dRef);

                        break;
                    }

                    case 3:
                    {
                        Unsafe.Add(ref dRef, 2) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 2),
                                                                          y,
                                                                          z);
                        goto case 2;
                    }

                    case 2:
                    {
                        Unsafe.Add(ref dRef, 1) = TTernaryOperator.Invoke(Unsafe.Add(ref xRef, 1),
                                                                          y,
                                                                          z);
                        goto case 1;
                    }

                    case 1:
                    {
                        dRef = TTernaryOperator.Invoke(xRef, y, z);
                        goto case 0;
                    }

                    case 0:
                    {
                        break;
                    }
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void VectorizedSmall8(ref T xRef, T y, T z, ref T dRef, nuint remainder)
            {
                Debug.Assert(sizeof(T) == 8);

                switch (remainder)
                {
                    case 7:
                    case 6:
                    case 5:
                    {
                        Debug.Assert(Vector256.IsHardwareAccelerated);

                        Vector256<T> yVec = Vector256.Create(y);
                        Vector256<T> zVec = Vector256.Create(z);

                        Vector256<T> beg = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef),
                                                                   yVec,
                                                                   zVec);
                        Vector256<T> end = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef, remainder - (uint)Vector256<T>.Count),
                                                                   yVec,
                                                                   zVec);

                        beg.StoreUnsafe(ref dRef);
                        end.StoreUnsafe(ref dRef, remainder - (uint)Vector256<T>.Count);

                        break;
                    }

                    case 4:
                    {
                        Debug.Assert(Vector256.IsHardwareAccelerated);

                        Vector256<T> beg = TTernaryOperator.Invoke(Vector256.LoadUnsafe(ref xRef),
                                                                   Vector256.Create(y),
                                                                   Vector256.Create(z));
                        beg.StoreUnsafe(ref dRef);

                        break;
                    }

                    case 3:
                    {
                        Debug.Assert(Vector128.IsHardwareAccelerated);

                        Vector128<T> yVec = Vector128.Create(y);
                        Vector128<T> zVec = Vector128.Create(z);

                        Vector128<T> beg = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef),
                                                                   yVec,
                                                                   zVec);
                        Vector128<T> end = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef, remainder - (uint)Vector128<T>.Count),
                                                                   yVec,
                                                                   zVec);

                        beg.StoreUnsafe(ref dRef);
                        end.StoreUnsafe(ref dRef, remainder - (uint)Vector128<T>.Count);

                        break;
                    }

                    case 2:
                    {
                        Debug.Assert(Vector128.IsHardwareAccelerated);

                        Vector128<T> beg = TTernaryOperator.Invoke(Vector128.LoadUnsafe(ref xRef),
                                                                   Vector128.Create(y),
                                                                   Vector128.Create(z));
                        beg.StoreUnsafe(ref dRef);

                        break;
                    }

                    case 1:
                    {
                        dRef = TTernaryOperator.Invoke(xRef, y, z);
                        goto case 0;
                    }

                    case 0:
                    {
                        break;
                    }
                }
            }
        }
    }
}
