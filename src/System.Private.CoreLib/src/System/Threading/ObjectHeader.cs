// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics.Contracts;
using System.Runtime.CompilerServices;

namespace System.Threading
{
    /// <summary>
    /// Manipulates the object header located 4 bytes before each object's EEType pointer
    /// in the managed heap.
    /// </summary>
    /// <remarks>
    /// Do not store managed pointers (ref int) to the object header in locals or parameters
    /// as they may be incorrectly updated during garbage collection.
    /// </remarks>
    internal static class ObjectHeader
    {
        // The following two header bits are used by the GC engine:
        //   BIT_SBLK_FINALIZER_RUN = 0x40000000
        //   BIT_SBLK_GC_RESERVE    = 0x20000000
        //
        // All other bits may be used to store runtime data: hash code, sync entry index, etc.
        // Here we use the same bit layout as in CLR: if bit 26 (BIT_SBLK_IS_HASHCODE) is set,
        // all the lower bits 0..25 store the hash code, otherwise they store either the sync
        // entry index or all zero.
        //
        // If needed, the MASK_HASHCODE_INDEX bit mask may be made wider or narrower than the
        // current 26 bits; the BIT_SBLK_IS_HASHCODE bit is not required to be adjacent to the
        // mask.  The code only assumes that MASK_HASHCODE_INDEX occupies the lowest bits of the
        // header (i.e. ends with bit 0) and that (MASK_HASHCODE_INDEX + 1) does not overflow
        // the Int32 type (i.e. the mask may be no longer than 30 bits).

        private  const int BIT_SBLK_IS_HASHCODE = 0x04000000;
        internal const int MASK_HASHCODE_INDEX  = BIT_SBLK_IS_HASHCODE - 1;

        /// <summary>
        /// Returns the hash code assigned to the object.  If no hash code has yet been assigned,
        /// it assigns one in a thread-safe way.
        /// </summary>
        public static unsafe int GetHashCode(object o)
        {
            if (o == null)
            {
                return 0;
            }

            fixed (IntPtr* pEEType = &o.m_pEEType)
            {
                // The header is 4 bytes before m_pEEType field on all architectures
                int* pHeader = (int*)pEEType - 1;
                int bits = *pHeader;
                int hashOrIndex = bits & MASK_HASHCODE_INDEX;
                if ((bits & BIT_SBLK_IS_HASHCODE) != 0)
                {
                    // Found the hash code in the header
                    Contract.Assert(hashOrIndex != 0);
                    return hashOrIndex;
                }
                if (hashOrIndex != 0)
                {
                    // Look up the hash code in the SyncTable
                    int hashCode = SyncTable.GetHashCode(hashOrIndex);
                    if (hashCode != 0)
                    {
                        return hashCode;
                    }
                }
                // The hash code has not yet been set.  Assign some value.
                return AssignHashCode(pHeader);
            }
        }

        /// <summary>
        /// Assigns a hash code to the object in a thread-safe way.
        /// </summary>
        private static unsafe int AssignHashCode(int* pHeader)
        {
            int newHash = RuntimeHelpers.GetNewHashCode() & MASK_HASHCODE_INDEX;
            int bitAndValue;

            // Never use the zero hash code.  SyncTable treats the zero value as "not assigned".
            if (newHash == 0)
            {
                newHash = 1;
            }

            while (true)
            {
                int oldBits = Volatile.Read(ref *pHeader);
                bitAndValue = oldBits & (BIT_SBLK_IS_HASHCODE | MASK_HASHCODE_INDEX);
                if (bitAndValue != 0)
                {
                    // The header already stores some value
                    break;
                }

                // The header stores nothing.  Try to store the hash code.
                int newBits = oldBits | BIT_SBLK_IS_HASHCODE | newHash;
                if (Interlocked.CompareExchange(ref *pHeader, newBits, oldBits) == oldBits)
                {
                    return newHash;
                }

                // Another thread modified the header; try again
            }

            if ((bitAndValue & BIT_SBLK_IS_HASHCODE) == 0)
            {
                // Set the hash code in SyncTable.  This call will resolve the potential race.
                return SyncTable.SetHashCode(bitAndValue, newHash);
            }

            // Another thread set the hash code, use it
            Contract.Assert((bitAndValue & ~BIT_SBLK_IS_HASHCODE) != 0);
            return bitAndValue & ~BIT_SBLK_IS_HASHCODE;
        }

        /// <summary>
        /// Extracts the sync entry index or the hash code from the header value.  Returns true
        /// if the header value stores the sync entry index.
        /// </summary>
        public static bool GetSyncEntryIndex(int header, out int hashOrIndex)
        {
            hashOrIndex = header & MASK_HASHCODE_INDEX;
            return (hashOrIndex != 0) && ((header & BIT_SBLK_IS_HASHCODE) == 0);
        }

        /// <summary>
        /// Returns the Monitor synchronization object assigned to this object.  If no synchronization
        /// object has yet been assigned, it assigns one in a thread-safe way.
        /// </summary>
        public static unsafe Lock GetLockObject(object o)
        {
            fixed (IntPtr* pEEType = &o.m_pEEType)
            {
                int* pHeader = (int*)pEEType - 1;
                int hashOrIndex;

                if (GetSyncEntryIndex(*pHeader, out hashOrIndex))
                {
                    // Already have a sync entry for this object, return the synchronization object
                    // stored in the entry.
                    return SyncTable.GetLockObject(hashOrIndex);
                }
                // Assign a new sync entry
                int syncIndex = SyncTable.AssignEntry(o, pHeader);
                return SyncTable.GetLockObject(syncIndex);
            }
        }

        /// <summary>
        /// Sets the sync entry index in a thread-safe way.
        /// </summary>
        public static unsafe void SetSyncEntryIndex(int* pHeader, int syncIndex)
        {
            // Holding this lock implies there is at most one thread setting the sync entry index at
            // any given time.  We also require that the sync entry index has not been already set.
            Contract.Assert(SyncTable.s_freeEntriesLock.IsAcquired);
            Contract.Assert((syncIndex & MASK_HASHCODE_INDEX) == syncIndex);
            int oldBits, newBits, hashOrIndex;

            do
            {
                oldBits = Volatile.Read(ref *pHeader);
                newBits = oldBits;

                if (GetSyncEntryIndex(oldBits, out hashOrIndex))
                {
                    // Must not get here; see the contract
                    throw new InvalidOperationException();
                }

                Contract.Assert(((oldBits & BIT_SBLK_IS_HASHCODE) == 0) || (hashOrIndex != 0));
                if (hashOrIndex != 0)
                {
                    // Move the hash code to the sync entry
                    SyncTable.MoveHashCodeToNewEntry(syncIndex, hashOrIndex);
                }

                // Store the sync entry index
                newBits &= ~(BIT_SBLK_IS_HASHCODE | MASK_HASHCODE_INDEX);
                newBits |= syncIndex;
            }
            while (Interlocked.CompareExchange(ref *pHeader, newBits, oldBits) != oldBits);
        }
    }
}