// Copyright (c) Mixed Reality Toolkit Contributors
// Licensed under the BSD 3-Clause

using System;
using UnityEngine;

namespace MixedReality.Toolkit.Input
{
    /// <summary>
    /// Simple script that makes the attached GameObject follow the specified joint.
    /// Internal for now; TODO possibly made public in the future.
    /// </summary>
    /// <remarks>
    /// Packaging-wise, this should be considered part of the Hands feature. This also does
    /// not depend on XRI.
    /// </remarks>
    [AddComponentMenu("MRTK/Input/Follow Joint")]
    internal class FollowJoint : MonoBehaviour, ISerializationCallbackReceiver
    {
        [SerializeField]
        [Tooltip("The pose source representing the hand joint this interactor tracks")]
        private HandJointPoseSource jointPoseSource;

        /// <summary>
        /// The pose source representing the hand joint this interactor tracks
        /// </summary>
        protected HandJointPoseSource JointPoseSource { get => jointPoseSource; set => jointPoseSource = value; }

        // A temporary variable used to migrate instances of FollowJoint to use the jointPoseSource class as the source of truth
        // rather than its own separately serialized values.
        // TODO: Remove this after some time to ensure users have successfully migrated.
        [SerializeField, HideInInspector]
        private bool migratedSuccessfully = false;

        [SerializeField]
        [HideInInspector]
        private Handedness hand;

        [SerializeField]
        [HideInInspector]
        private TrackedHandJoint joint;

        #region ISerializationCallbackReceiver

        void ISerializationCallbackReceiver.OnBeforeSerialize() { }

        /// <summary>
        /// Using ISerializationCallbackReceiver to ensure that instances of FollowJoint are migrated to the new HandJointPoseSource
        /// Doesn't work perfectly due to complications with prefab variants :(
        ///
        /// TODO: Remove this after some time to ensure users have successfully migrated.
        /// </summary>
        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            if (!migratedSuccessfully)
            {
                JointPoseSource.Hand = hand;
                JointPoseSource.Joint = joint;
                migratedSuccessfully = true;
            }
        }

        #endregion ISerializationCallbackReceiver

        /// <summary>
        /// A Unity event function that is called every frame, if this object is enabled.
        /// </summary>
        private void Update()
        {
            if (JointPoseSource != null && JointPoseSource.TryGetPose(out Pose pose))
            {
                transform.SetPositionAndRotation(pose.position, pose.rotation);
            }
            else
            {
                // If we have no valid tracked joint, reset to local zero.
                transform.localPosition = Vector3.zero;
                transform.localRotation = Quaternion.identity;
            }
        }
    }
}
