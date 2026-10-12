using UnityEngine;
using Oculus.Interaction;

// Interaction SDK transformer setup shared by the sheet tools and the UI panels.
public static class GrabTransformers
{
    // Gets or adds an unconstrained two-hand rotate transformer on the object.
    public static TwoGrabRotateTransformer EnsureYaw(GameObject go)
    {
        TwoGrabRotateTransformer yaw = go.GetComponent<TwoGrabRotateTransformer>();
        if (yaw != null) return yaw;

        yaw = go.AddComponent<TwoGrabRotateTransformer>();
        yaw.InjectOptionalConstraints(new TwoGrabRotateTransformer.TwoGrabRotateConstraints
        {
            MinAngle = new FloatConstraint(),
            MaxAngle = new FloatConstraint()
        });
        return yaw;
    }
}
