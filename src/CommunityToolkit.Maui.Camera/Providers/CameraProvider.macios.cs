using AVFoundation;
using CoreMedia;
using CoreVideo;
using UIKit;

namespace CommunityToolkit.Maui.Core;

partial class CameraProvider
{
	static readonly AVCaptureDeviceType[] captureDevices = InitializeCaptureDevices();

	static AVCaptureDeviceType[] InitializeCaptureDevices()
	{
		AVCaptureDeviceType[] deviceTypes = [];

		// We add devices in order of preference, so that the first device is the best one available.
		if (UIDevice.CurrentDevice.CheckSystemVersion(13, 0))
		{
			deviceTypes = [.. deviceTypes,
				AVCaptureDeviceType.BuiltInTripleCamera,
				AVCaptureDeviceType.BuiltInDualWideCamera,
				];
		}

		deviceTypes = [.. deviceTypes,
			AVCaptureDeviceType.BuiltInDualCamera,
			AVCaptureDeviceType.BuiltInWideAngleCamera,
			AVCaptureDeviceType.BuiltInTelephotoCamera,
		];

		if (UIDevice.CurrentDevice.CheckSystemVersion(13, 0))
		{
			deviceTypes = [.. deviceTypes,
				AVCaptureDeviceType.BuiltInUltraWideCamera,
				];
		}

		if (UIDevice.CurrentDevice.CheckSystemVersion(11, 1))
		{
			deviceTypes = [.. deviceTypes,
				AVCaptureDeviceType.BuiltInTrueDepthCamera];
		}

		if (UIDevice.CurrentDevice.CheckSystemVersion(15, 4))
		{
			deviceTypes = [.. deviceTypes,
				AVCaptureDeviceType.BuiltInLiDarDepthCamera];
		}

		return deviceTypes;
	}

	private partial ValueTask PlatformRefreshAvailableCameras(CancellationToken token)
	{
		var discoverySession = AVCaptureDeviceDiscoverySession.Create(captureDevices, AVMediaTypes.Video, AVCaptureDevicePosition.Unspecified);
		var availableCameras = new List<CameraInfo>();

		foreach (var device in discoverySession.Devices)
		{
			if (!device.Connected)
			{
				continue;
			}

			var position = device.Position switch
			{
				AVCaptureDevicePosition.Front => CameraPosition.Front,
				AVCaptureDevicePosition.Back => CameraPosition.Rear,
				AVCaptureDevicePosition.Unspecified => CameraPosition.Unknown,
				_ => throw new NotSupportedException($"{device.Position} is not yet supported")
			};

			var supportedFormats = new List<AVCaptureDeviceFormat>();
			var supportedResolutions = new List<Size>();

			foreach (var format in device.Formats)
			{
				var dimension = ((CMVideoFormatDescription)format.FormatDescription).Dimensions;

				if ((int)format.FormatDescription.VideoCodecType == (int)CVPixelFormatType.CV420YpCbCr8BiPlanarVideoRange)
				{
					continue;
				}

				if (supportedResolutions.Contains(new(dimension.Width, dimension.Height)))
				{
					continue;
				}

				supportedFormats.Add(format);
				supportedResolutions.Add(new(dimension.Width, dimension.Height));
			}

			if (supportedFormats.Count is 0)
			{
				continue;
			}

			float zoomMultiplier = 1.0f;
			if (OperatingSystem.IsIOSVersionAtLeast(18))
			{
				zoomMultiplier = (float)device.DisplayVideoZoomFactorMultiplier;
			}

			var cameraInfo = new CameraInfo(device.LocalizedName,
				device.UniqueID,
				position,
				device.HasFlash,
				(float)device.MinAvailableVideoZoomFactor,
				(float)device.MaxAvailableVideoZoomFactor,
				zoomMultiplier,
				supportedResolutions,
				device.VirtualDevice,
				device,
				supportedFormats
	);

			availableCameras.Add(cameraInfo);
		}

		AvailableCameras = availableCameras;

		return ValueTask.CompletedTask;
	}
}