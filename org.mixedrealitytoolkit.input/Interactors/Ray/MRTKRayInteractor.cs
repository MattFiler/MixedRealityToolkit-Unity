// Copyright (c) Mixed Reality Toolkit Contributors
// Licensed under the BSD 3-Clause

using System;
using System.Collections.Generic;
using MixedReality.Toolkit.Subsystems;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace MixedReality.Toolkit.Input
{
    /// <summary>
    /// A wrapper for the <see cref="XRRayInteractor"/> which stores extra information for MRTK management/services
    /// </summary>
    [AddComponentMenu("MRTK/Input/MRTK Ray Interactor")]
    // This execution order ensures that the MRTKRayInteractor runs its update function right after the
    // <see cref="XRController"/>. We do this because the <see cref="MRTKRayInteractor"/> needs to set its own pose after the parent controller transform,
    // but before any physics raycast calls are made to determine selection. The earliest a physics call can be made is within
    // the UIInputModule, which has an update order much higher than <see cref="XRController"/>s.
    // TODO: Examine the update order of other interactors in the future with respect to when their physics calls happen,
    // or create a system to keep ensure interactor poses aren't ever implicitly set via parenting.
    [DefaultExecutionOrder(XRInteractionUpdateOrder.k_Controllers + 1)]
    public class MRTKRayInteractor :
        XRRayInteractor,
        IRayInteractor,
        IHandedInteractor,
        IVariableSelectInteractor
    {
        #region MRTKRayInteractor

        [SerializeField, Tooltip("Holds a reference to the <see cref=\"TrackedPoseDriver\"/> associated to this interactor if it exists.")]
        private TrackedPoseDriver trackedPoseDriver = null;

        /// <summary>
        /// Holds a reference to the <see cref="TrackedPoseDriver"/> associated to this interactor if it exists.
        /// </summary>
        private TrackedPoseDriver TrackedPoseDriver => trackedPoseDriver;

        /// <summary>
        /// Is this ray currently hovering a UnityUI/Canvas element?
        /// </summary>
        public bool HasUIHover => TryGetUIModel(out TrackedDeviceModel model) && model.currentRaycast.isValid;

        /// <summary>
        /// Is this ray currently selecting a UnityUI/Canvas element?
        /// </summary>
        public bool HasUISelection => HasUIHover && isUISelectActive;

        /// <summary>
        /// Used to check if the parent controller is tracked or not
        /// Hopefully this becomes part of the base Unity XRI API.
        /// </summary>
        private bool IsTracked
        {
            get
            {
#pragma warning disable CS0618 // Type or member is obsolete
                if (xrController == null) //If no XRController is associated with this interactor then try to get the TrackedPoseDriver component instead
                {
                    if (TrackedPoseDriver == null) //If the interactor does not have a TrackedPoseDriver component then it is not tracked
                    {
                        return false;
                    }

                    //If this interactor has a TrackedPoseDriver then use it to check if this interactor is tracked
                    return ((InputTrackingState)TrackedPoseDriver.trackingStateInput.action.ReadValue<int>()).HasPositionAndRotation();
                }

                //If the XRController has already been set then use it to check if the controller is tracked
                return xrController.currentControllerState.inputTrackingState.HasPositionAndRotation();
#pragma warning restore CS0618
            }
        }

        /// <summary>
        /// Cached reference to hands aggregator for efficient per-frame use.
        /// </summary>
        [Obsolete("Deprecated, please use XRSubsystemHelpers.HandsAggregator instead.")]
        protected HandsAggregatorSubsystem HandsAggregator => XRSubsystemHelpers.HandsAggregator as HandsAggregatorSubsystem;

        /// <summary>
        /// How unselected the interactor must be to initiate a new hover or selection on a new target.
        /// Separate from the hand controller's threshold for pinching, so that we can tune
        /// overall pinching threshold separately from the roll-off prevention.
        /// Should be [0,1].
        /// </summary>
        /// <remarks>
        /// May be made serialized + exposed in the future.
        /// Larger than the relaxation threshold on <see cref="GazePinchInteractor"/>, as fewer
        /// accidental activations will occur with rays.
        /// </remarks>
        protected internal float relaxationThreshold = 0.5f;

        // Whether the hand has relaxed (i.e. fully unselected) pre-selection.
        // Used to prevent accidental activations by requiring a full selection motion to complete.
        private bool isRelaxedBeforeSelect = false;

        private float refDistance = 0;

        private Pose initialLocalAttach = Pose.identity;

        #endregion MRTKRayInteractor

        #region IHandedInteractor

        Handedness IHandedInteractor.Handedness
        {
            get
            {
#pragma warning disable CS0618 // Type or member is obsolete
#pragma warning disable CS0612 // Type or member is obsolete
                if (xrController != null)
                {
                    return (xrController is ArticulatedHandController handController) ? handController.HandNode.ToHandedness() : Handedness.None;
                }
#pragma warning restore CS0612
#pragma warning restore CS0618
                else
                {
                    return handedness.ToMRTKHandedness();
                }
            }
        }

        #endregion IHandedInteractor

        #region IVariableSelectInteractor

        /// <inheritdoc />
        public float SelectProgress
        {
            get
            {
#pragma warning disable CS0618 // Type or member is obsolete
                if (xrController != null)
                {
                    return xrController.selectInteractionState.value;
                }
#pragma warning restore CS0618
                else if (selectInput != null)
                {
                    return selectInput.ReadValue();
                }
                else
                {
                    Debug.LogWarning($"Unable to determine SelectProgress of {name} because there is neither an XRBaseController nor the Input Configuration has Select Input Actions referenced to it.");
                }
                return 0;
            }
        }

        #endregion IVariableSelectInteractor

        #region XRBaseInteractor

        /// <inheritdoc />
        public override bool CanHover(IXRHoverInteractable interactable)
        {
            // We stay hovering if we have selected anything.
            bool stickyHover = hasSelection && IsSelecting(interactable);
            if (stickyHover)
            {
                return true;
            }

            // We are ready to pinch if we are in the PinchReady position,
            // or if we are already selecting something.
            bool ready = isHoverActive || isSelectActive;

            // Is this a new interactable that we aren't already hovering?
            bool isNew = !IsHovering(interactable);

            // If so, should we be allowed to initiate a new hover on it?
            // This prevents us from "rolling off" one target and immediately
            // semi-pressing another.
            bool canHoverNew = !isNew || SelectProgress < relaxationThreshold;

            return ready && base.CanHover(interactable) && canHoverNew;
        }

        /// <inheritdoc />
        public override bool CanSelect(IXRSelectInteractable interactable)
        {
            return base.CanSelect(interactable) && (!hasSelection || IsSelecting(interactable)) && isRelaxedBeforeSelect;
        }

        /// <inheritdoc />
        public override void GetValidTargets(List<IXRInteractable> targets)
        {
            // When selection is active, force valid targets to be the current selection. This is done to ensure that selected objects remained hovered.
            if (hasSelection && isActiveAndEnabled)
            {
                targets.Clear();
                for (int i = 0; i < interactablesSelected.Count; i++)
                {
                    targets.Add(interactablesSelected[i]);
                }
            }
            else
            {
                base.GetValidTargets(targets);
            }
        }

        /// <inheritdoc />
        public override bool isHoverActive
        {
            get
            {
                // When the gaze pinch interactor is already selecting an object, use the default interactor behavior
                if (hasSelection)
                {
                    return base.isHoverActive && IsTracked;
                }
                // Otherwise, this selector is only allowed to hover if we can tell that the palm for the corresponding hand/controller is facing away from the user.
                else
                {
                    bool hoverActive = base.isHoverActive;
                    if (hoverActive)
                    {
                        bool isPalmFacingAway = false;

#pragma warning disable CS0618 // Type or member is obsolete
#pragma warning disable CS0612 // Type or member is obsolete
                        if (xrController != null)
                        {
                            if (xrController is ArticulatedHandController handController)
                            {
                                if (XRSubsystemHelpers.HandsAggregator?.TryGetPalmFacingAway(handController.HandNode, out isPalmFacingAway) ?? true)
                                {
                                    hoverActive &= isPalmFacingAway;
                                }
                            }
                        }
#pragma warning restore CS0612
#pragma warning restore CS0618
                        else
                        {
                            if (XRSubsystemHelpers.HandsAggregator?.TryGetPalmFacingAway(handedness.ToXRNode(), out isPalmFacingAway) ?? true)
                            {
                                hoverActive &= isPalmFacingAway;
                            }
                        }
                    }

                    return hoverActive && IsTracked;
                }
            }
        }

        [SerializeReference]
        [InterfaceSelector(true)]
        [Tooltip("The pose source representing the pose this interactor uses for aiming and positioning. Follows the 'pointer pose'")]
        private IPoseSource aimPoseSource;

        /// <summary>
        /// The pose source representing the ray this interactor uses for aiming and positioning.
        /// </summary>
        protected IPoseSource AimPoseSource { get => aimPoseSource; set => aimPoseSource = value; }

        [SerializeReference]
        [InterfaceSelector(true)]
        [Tooltip("The pose source representing the device this interactor uses for rotation.")]
        private IPoseSource devicePoseSource;

        /// <summary>
        /// The pose source representing the device this interactor uses for rotation.
        /// </summary>
        protected IPoseSource DevicePoseSource { get => devicePoseSource; set => devicePoseSource = value; }

        private static readonly ProfilerMarker ProcessInteractorPerfMarker =
            new ProfilerMarker("[MRTK] MRTKRayInteractor.ProcessInteractor");

        /// <inheritdoc />
        public override void ProcessInteractor(XRInteractionUpdateOrder.UpdatePhase updatePhase)
        {
            base.ProcessInteractor(updatePhase);

            using (ProcessInteractorPerfMarker.Auto())
            {
                if (updatePhase == XRInteractionUpdateOrder.UpdatePhase.Dynamic)
                {
                    // If we've fully relaxed, we can begin hovering/selecting a new target.
                    if (SelectProgress < relaxationThreshold)
                    {
                        isRelaxedBeforeSelect = true;
                    }
                    // If we're not relaxed, and we aren't currently hovering or selecting anything,
                    // we can't initiate new hovers or selections.
                    else if (!hasHover && !hasSelection)
                    {
                        isRelaxedBeforeSelect = false;
                    }
                }
            }
        }

        /// <inheritdoc />
        protected override void OnSelectEntering(SelectEnterEventArgs args)
        {
            base.OnSelectEntering(args);

            initialLocalAttach = new Pose(attachTransform.localPosition, attachTransform.localRotation);
            refDistance = PoseUtilities.GetDistanceToBody(new Pose(transform.position, transform.rotation));
        }

        #endregion XRBaseInteractor

        /// <inheritdoc/>
        protected override void Start()
        {
            base.Start();

            if (trackedPoseDriver == null) //Try to get the TrackedPoseDriver component from the parent if it hasn't been set yet
            {
                trackedPoseDriver = GetComponentInParent<TrackedPoseDriver>();
            }
        }

        /// <summary>
        /// A Unity event function that is called every frame, if this object is enabled.
        /// </summary>
        private void Update()
        {
            // Use Pose Sources to calculate the interactor's pose and the attach transform's position
            // We have to make sure the ray interactor is oriented appropriately before calling
            // lower level raycasts
            if (AimPoseSource != null && AimPoseSource.TryGetPose(out Pose aimPose))
            {
                transform.SetPositionAndRotation(aimPose.position, aimPose.rotation);

                if (hasSelection)
                {
                    float distanceRatio = PoseUtilities.GetDistanceToBody(aimPose) / refDistance;
                    attachTransform.localPosition = new Vector3(initialLocalAttach.position.x, initialLocalAttach.position.y, initialLocalAttach.position.z * distanceRatio);
                }
            }

            // Use the Device Pose Sources to calculate the attach transform's pose
            if (DevicePoseSource != null && DevicePoseSource.TryGetPose(out Pose devicePose))
            {
                attachTransform.rotation = devicePose.rotation;
            }
        }
    }
}
