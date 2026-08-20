using OpenCvSharp;

namespace ExpressPackingMonitoring.Services;

internal static class CameraFrameOrientation
{
    internal static void Apply(Mat frame, bool rotate180)
    {
        Apply(frame, rotate180 ? 180 : 0);
    }

    internal static void Apply(Mat frame, int rotation)
    {
        if (frame.Empty())
            return;

        switch (Normalize(rotation))
        {
            case 90:
                Cv2.Rotate(frame, frame, RotateFlags.Rotate90Clockwise);
                break;
            case 180:
                Cv2.Rotate(frame, frame, RotateFlags.Rotate180);
                break;
            case 270:
                Cv2.Rotate(frame, frame, RotateFlags.Rotate90Counterclockwise);
                break;
        }
    }

    internal static int Normalize(int rotation) => rotation switch
    {
        90 or 180 or 270 => rotation,
        _ => 0
    };
}
