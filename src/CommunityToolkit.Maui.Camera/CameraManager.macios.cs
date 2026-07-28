using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using AVFoundation;
using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Primitives;
using CoreMedia;
using CoreMotion;
using Foundation;
using ObjCRuntime;
using UIKit;

namespace CommunityToolkit.Maui.Core;

partial class CameraManager
{
	// TODO: Check if we really need this
	readonly NSDictionary<NSString, NSObject> codecSettings = new([AVVideo.CodecKey], [new NSString("jpeg")]);
	AVCaptureDeviceInput? audioInput;
	AVCaptureDevice? captureDevice;
	AVCaptureDeviceInput? captureInput;

	AVCaptureSession? captureSession;

	AVCaptureFlashMode flashMode;

	IDisposable? orientationDidChangeObserver;
	AVCapturePhotoOutput? photoOutput;
	PreviewView? previewView;

	AVCaptureVideoOrientation videoOrientation;  // iOS <17
	nfloat videoRotationAngle;                   // iOS 17+
	AVCaptureMovieFileOutput? videoOutput;
	AVCaptureMovieFileOutputRecordingDelegate? videoFinalizationDelegate;
	AVCaptureDeviceRotationCoordinator? rotationCoordinator;
	string? videoRecordingFileName;
	bool deleteVideoRecordingFileOnCompletion;
	TaskCompletionSource? videoRecordingFinalizeTcs;
	Stream? videoRecordingStream;
	CMMotionManager? motionManager;

	/// <inheritdoc />
	public void Dispose()
	{
		CleanupVideoRecordingResources();

		captureSession?.StopRunning();
		captureSession?.Dispose();
		captureSession = null;

		captureInput?.Dispose();
		captureInput = null;

		captureDevice = null;

		orientationDidChangeObserver?.Dispose();
		orientationDidChangeObserver = null;

		photoOutput?.Dispose();
		photoOutput = null;

		previewView?.Dispose();
		previewView = null;

		videoRecordingStream?.Dispose();
		videoRecordingStream = null;

		rotationCoordinator?.Dispose();
		rotationCoordinator = null;

		motionManager?.StopAccelerometerUpdates();
		motionManager?.Dispose();
		motionManager = null;
	}

	public NativePlatformCameraPreviewView CreatePlatformView()
	{
		captureSession = new AVCaptureSession
		{
			SessionPreset = AVCaptureSession.PresetPhoto
		};

		previewView = new PreviewView
		{
			Session = captureSession
		};

		// use CMMotionManager to get device orientation on iOS 16 or lower, since AVCaptureDeviceRotationCoordinator is unavailable
		if (!UIDevice.CurrentDevice.CheckSystemVersion(17, 0))
		{
			motionManager ??= new();
			motionManager.StartAccelerometerUpdates();
			// ObserveOrientationDidChange is only needed on iOS <17; on iOS 17+ the rotationCoordinator handles this
			orientationDidChangeObserver = UIDevice.Notifications.ObserveOrientationDidChange((_, _) => UpdateVideoOrientation());
		}
		UpdateVideoOrientation();

		return previewView;
	}

	public partial void UpdateFlashMode(CameraFlashMode flashMode)
	{
		this.flashMode = flashMode.ToPlatform();
	}

	public partial void UpdateIsTorchOn(bool isTorchOn)
	{
		if (!isInitialized ||
			captureDevice is null ||
			!captureDevice.TorchAvailable)
		{
			return;
		}

		bool isCurrentlyOn = captureDevice.TorchActive;

		if (isCurrentlyOn != isTorchOn)
		{
			captureDevice.LockForConfiguration(out NSError? error);
			if (error is not null)
			{
				Trace.WriteLine(error);
				return;
			}

			captureDevice.TorchMode = isTorchOn ? AVCaptureTorchMode.On : AVCaptureTorchMode.Off;
			captureDevice.UnlockForConfiguration();
		}
	}

	public partial void UpdateZoom(float zoomLevel)
	{
		if (!isInitialized || captureDevice is null)
		{
			return;
		}

		if (zoomLevel < (float)captureDevice.MinAvailableVideoZoomFactor || zoomLevel > (float)captureDevice.MaxAvailableVideoZoomFactor)
		{
			return;
		}

		captureDevice.LockForConfiguration(out NSError? error);
		if (error is not null)
		{
			Trace.WriteLine(error);
			return;
		}

		captureDevice.VideoZoomFactor = zoomLevel;
		captureDevice.UnlockForConfiguration();
	}

	public partial ValueTask UpdateCaptureResolution(Size resolution, CancellationToken token)
	{
		if (cameraView.SelectedCamera is null || captureDevice is null)
		{
			return ValueTask.CompletedTask;
		}

		captureDevice.LockForConfiguration(out NSError? error);
		if (error is not null)
		{
			Trace.WriteLine(error);
			return ValueTask.CompletedTask;
		}

		var formatsMatchingResolution = cameraView.SelectedCamera.SupportedFormats
			.Where(format => MatchesResolution(format, resolution))
			.ToList();

		var availableFormats = formatsMatchingResolution.Count is not 0
			? formatsMatchingResolution
			: GetPhotoCompatibleFormats(cameraView.SelectedCamera.SupportedFormats);

		var selectedFormat = availableFormats
			.OrderByDescending(f => f.ResolutionArea)
			.FirstOrDefault();

		if (selectedFormat is not null)
		{
			captureDevice.ActiveFormat = selectedFormat;
		}

		captureDevice.UnlockForConfiguration();
		return ValueTask.CompletedTask;
	}

	public partial Size? GetPreviewSize()
	{
		if (captureDevice is null)
		{
			return null;
		}

		var sz = ((CMVideoFormatDescription)captureDevice.ActiveFormat.FormatDescription).Dimensions;

		bool isLandscape;
		if (UIDevice.CurrentDevice.CheckSystemVersion(17, 0))
		{
			// 90° and 270° angles correspond to landscape orientations
			isLandscape = videoRotationAngle == 90 || videoRotationAngle == 270;
		}
		else
		{
			isLandscape = videoOrientation is AVCaptureVideoOrientation.LandscapeLeft
										   or AVCaptureVideoOrientation.LandscapeRight;
		}

		return isLandscape
			? new Size(sz.Width, sz.Height)
			: new Size(sz.Height, sz.Width);
	}

	private async partial Task PlatformConnectCamera(CancellationToken token)
	{
		await PlatformStartCameraPreview(token);
	}

	private async partial Task PlatformStartCameraPreview(CancellationToken token)
	{
		if (captureSession is null)
		{
			return;
		}

		captureSession.BeginConfiguration();

		foreach (var input in captureSession.Inputs)
		{
			captureSession.RemoveInput(input);
			input.Dispose();
		}

		cameraView.SelectedCamera ??= cameraProvider.AvailableCameras?.FirstOrDefault() ?? throw new CameraException("No camera available on device");

		captureDevice = cameraView.SelectedCamera.CaptureDevice ?? throw new CameraException($"No Camera found");
		captureInput = new AVCaptureDeviceInput(captureDevice, out NSError? error);

		if (error is null && captureSession.CanAddInput(captureInput))
		{
			captureSession.AddInput(captureInput);
		}
		else
		{
			var errorMessage = error is not null
				? $"Error creating capture device input: {error.LocalizedDescription}"
				: "Unable to add capture device input to capture session.";

			captureInput.Dispose();
			captureInput = null;
			captureSession.CommitConfiguration();
			throw new CameraException(errorMessage);
		}

		// On iOS 17+, create a new instance of AVCaptureDeviceRotationCoordinator when switching to a new camera
		if (UIDevice.CurrentDevice.CheckSystemVersion(17, 0))
		{
			rotationCoordinator?.Dispose();
			rotationCoordinator = new(captureDevice, previewView?.Layer);
		}

		if (photoOutput is null)
		{
			photoOutput = new AVCapturePhotoOutput();
			captureSession.AddOutput(photoOutput);
		}

		await UpdateCaptureResolution(cameraView.ImageCaptureResolution, token);

		captureSession.CommitConfiguration();
		captureSession.StartRunning();
		isInitialized = true;
		onLoaded.Invoke();
	}

	private partial void PlatformStopCameraPreview()
	{
		if (captureSession is null)
		{
			return;
		}

		if (captureSession.Running)
		{
			captureSession.StopRunning();
		}

		isInitialized = false;
	}

	private partial void PlatformDisconnect()
	{
	}

	private partial void PlatformSetRegionOfInterest(Rect? regionOfInterest)
	{
		if (captureDevice is null)
		{
			return;
		}

		captureDevice.LockForConfiguration(out NSError? error);
		if (error is not null)
		{
			Trace.WriteLine(error);
			return;
		}

		if (regionOfInterest == Rect.Zero)
		{
			regionOfInterest = null;
		}

		var pt = regionOfInterest is null
			? new CoreGraphics.CGPoint(0.5, 0.5)
			: CalculateFocusPoint(regionOfInterest.Value);

		var focusMode = regionOfInterest == null 
			? AVCaptureFocusMode.ContinuousAutoFocus 
			: AVCaptureFocusMode.AutoFocus;

		var exposureMode = regionOfInterest == null
			? AVCaptureExposureMode.ContinuousAutoExposure
			: AVCaptureExposureMode.AutoExpose;

		Debug.WriteLine($"Poi: {pt}, {focusMode}, {exposureMode}");

		if (captureDevice.FocusPointOfInterestSupported)
		{
			captureDevice.FocusPointOfInterest = pt;
		}

		if (captureDevice.IsFocusModeSupported(focusMode))
		{
			captureDevice.FocusMode = focusMode;
		}

		if (captureDevice.ExposurePointOfInterestSupported)
		{
			captureDevice.ExposurePointOfInterest = pt;
		}

		if (captureDevice.IsExposureModeSupported(exposureMode))
		{
			captureDevice.ExposureMode = exposureMode;
		}

		captureDevice.UnlockForConfiguration();
	}

	CoreGraphics.CGPoint CalculateFocusPoint(Rect rect)
	{
		// AVFoundation's FocusPointOfInterest uses a coordinate system where {0,0} is the top-left
		// and {1,1} is the bottom-right, always relative to landscape orientation with the home button
		// on the right, regardless of actual device orientation.
		// When the device is in portrait, sensor landscape-X maps to portrait-Y (top-to-bottom),
		// and sensor landscape-Y maps to portrait-X (right-to-left, hence the 1-n inversion).
		// We assume a sensor aspect ratio of 4:3.
		const float SensorAspectRatio = 4f / 3f;

		var viewWidth = previewView?.Bounds.Width ?? cameraView.Width;
		var viewHeight = previewView?.Bounds.Height ?? cameraView.Height;
		var ar = viewWidth / viewHeight;

		double fullWidth, fullHeight;
		if (ar > SensorAspectRatio)
		{
			fullHeight = viewHeight / SensorAspectRatio;
			fullWidth = viewWidth;
		}
		else
		{
			fullHeight = viewHeight;
			fullWidth = viewWidth * SensorAspectRatio;
		}

		var tx = (fullHeight - viewHeight) * 0.5;
		var ty = (fullWidth - viewWidth) * 0.5;

		var nx = (float)Math.Clamp((rect.Center.Y + tx) / fullHeight, 0.0, 1.0);
		var ny = (float)Math.Clamp(1.0 - (rect.Center.X + ty) / fullWidth, 0.0, 1.0);

		return new CoreGraphics.CGPoint(nx, ny);
	}


	private async partial Task PlatformStartVideoRecording(Stream stream, CancellationToken token)
	{
		videoRecordingStream = stream;
		await PlatformStartVideoRecording(new VideoRecordingOptions(), token);
	}

	private async partial Task PlatformStartVideoRecording(VideoRecordingOptions options, CancellationToken token)
	{
		var isPermissionGranted = await AVCaptureDevice.RequestAccessForMediaTypeAsync(AVAuthorizationMediaType.Video).WaitAsync(token);
		if (!isPermissionGranted)
		{
			throw new CameraException("Camera permission is not granted. Please enable it in the app settings.");
		}

		if (captureSession is null)
		{
			throw new CameraException("Capture session is not initialized. Call ConnectCamera first.");
		}

		CleanupVideoRecordingResources();

		captureSession.BeginConfiguration();

		try
		{
			if (options.IncludeAudio)
			{
				var audioDevice = AVCaptureDevice.GetDefaultDevice(AVMediaTypes.Audio);
				if (audioDevice is not null)
				{
					audioInput = new AVCaptureDeviceInput(audioDevice, out NSError? audioError);
					if (audioError is null && captureSession.CanAddInput(audioInput))
					{
						captureSession.AddInput(audioInput);
					}
					else
					{
						audioInput?.Dispose();
						audioInput = null;
					}
				}
			}
		}
		catch
		{
			// Ignore audio configuration issues; proceed with video-only recording
		}

		videoOutput = new AVCaptureMovieFileOutput();

		if (!captureSession.CanAddOutput(videoOutput))
		{
			if (audioInput is not null)
			{
				captureSession.RemoveInput(audioInput);
				audioInput.Dispose();
				audioInput = null;
			}

			videoOutput?.Dispose();
			captureSession.CommitConfiguration();
			throw new CameraException("Unable to add video output to capture session.");
		}

		captureSession.AddOutput(videoOutput);

		// Select the best session preset allowed by MaxResolutionLines.
		// Preview and photo capture are unaffected; the preset is restored in CleanupVideoRecordingResources.
		foreach (var preset in CreateSessionPresets(options))
		{
			if (captureSession.CanSetSessionPreset(preset))
			{
				captureSession.SessionPreset = preset;
				break;
			}
		}

		captureSession.CommitConfiguration();

		// Changing the session preset resets VideoZoomFactor on the capture device to 1.0.
		// Re-apply the current zoom level so it is preserved when recording starts.
		UpdateZoom(cameraView.ZoomFactor);

		if (!TryConfigureAVCaptureConnection(videoOutput, out var error))
		{
			Trace.TraceWarning(error);
		}

		if (options.VideoCodec is not null)
		{
			ApplyVideoCodec(videoOutput, options.VideoCodec);
		}

		if (options.TargetFrameRate > 0 && captureDevice is not null)
		{
			if (captureDevice.LockForConfiguration(out NSError? frameRateError))
			{
				var supportedRange = captureDevice.ActiveFormat.VideoSupportedFrameRateRanges.FirstOrDefault();

				captureDevice.ActiveVideoMinFrameDuration = new CMTime(1, 25); // Minimum frame rate is 25 fps to avoid issues with some devices
				captureDevice.ActiveVideoMaxFrameDuration = new CMTime(1, Math.Clamp(options.TargetFrameRate, 25, (int)Math.Floor(supportedRange?.MaxFrameRate ?? 120)));
				captureDevice.UnlockForConfiguration();
			}
			else
			{
				Trace.TraceWarning($"Unable to set video frame rate: {frameRateError.LocalizedDescription}");
			}
		}

		videoRecordingFinalizeTcs = new TaskCompletionSource();

		videoRecordingFileName = options.SaveAsFilename ?? Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mov");
		deleteVideoRecordingFileOnCompletion = options.SaveAsFilename is null;

		var outputUrl = NSUrl.FromFilename(videoRecordingFileName);
		videoFinalizationDelegate = new AVCaptureMovieFileOutputRecordingDelegate(videoRecordingFinalizeTcs);
		videoOutput.StartRecordingToOutputFile(outputUrl, videoFinalizationDelegate);
	}

	private async partial Task<Stream> PlatformStopVideoRecording(CancellationToken token)
	{
		if (captureSession is null
			|| videoRecordingFileName is null
			|| videoOutput is null
			|| videoRecordingFinalizeTcs is null)
		{
			return Stream.Null;
		}

		videoOutput.StopRecording();
		await videoRecordingFinalizeTcs.Task.WaitAsync(token);
		videoFinalizationDelegate?.Dispose();
		videoFinalizationDelegate = null;

		if (videoRecordingStream is not null && File.Exists(videoRecordingFileName))
		{
			await using var inputStream = new FileStream(videoRecordingFileName, FileMode.Open, FileAccess.Read, FileShare.Read);
			await inputStream.CopyToAsync(videoRecordingStream, token);
			await videoRecordingStream.FlushAsync(token);
			if (videoRecordingStream.CanSeek)
			{
				videoRecordingStream.Position = 0;
			}
		}

		CleanupVideoRecordingResources();

		if (captureDevice is not null)
		{
			// Reset frame rate to default by setting min and max frame duration to invalid
			if (captureDevice.LockForConfiguration(out NSError? frameRateError))
			{
				captureDevice.ActiveVideoMinFrameDuration = CMTime.Invalid;
				captureDevice.ActiveVideoMaxFrameDuration = CMTime.Invalid;
				captureDevice.UnlockForConfiguration();
			}
		}

		return videoRecordingStream ?? Stream.Null;
	}

	void CleanupVideoRecordingResources()
	{
		if (captureSession is not null)
		{
			captureSession.BeginConfiguration();

			if (audioInput is not null)
			{
				captureSession.RemoveInput(audioInput);
				audioInput.Dispose();
			}

			if (videoOutput is not null)
			{
				captureSession.RemoveOutput(videoOutput);
				videoOutput.Dispose();
			}

			// Restore session preset to photo quality now that video recording has ended.
			if (captureSession.CanSetSessionPreset(AVCaptureSession.PresetPhoto))
			{
				captureSession.SessionPreset = AVCaptureSession.PresetPhoto;
			}

			captureSession.CommitConfiguration();

			// Changing the session preset resets VideoZoomFactor on the capture device to 1.0.
			// Re-apply the current zoom level so it is preserved when recording starts.
			UpdateZoom(cameraView.ZoomFactor);
		}

		videoOutput = null;
		audioInput = null;

		// Clean up temporary file
		if (videoRecordingFileName is not null)
		{
			if (deleteVideoRecordingFileOnCompletion && File.Exists(videoRecordingFileName))
			{
				File.Delete(videoRecordingFileName);
			}

			videoRecordingFileName = null;
		}

		videoRecordingFinalizeTcs = null;
	}

	private async partial ValueTask PlatformTakePicture(CancellationToken token)
	{
		ArgumentNullException.ThrowIfNull(photoOutput);

		var jpegWithQualityCodec = new NSDictionary<NSString, NSObject>([AVVideo.CodecKey, AVVideo.CompressionPropertiesKey],
			[new NSString("jpeg"), new NSDictionary<NSString, NSObject>([AVVideo.QualityKey], [new NSNumber(Math.Clamp(JpegQuality, 0.01, 1.0))])]);

		var capturePhotoSettings = AVCapturePhotoSettings.FromFormat(jpegWithQualityCodec);
		capturePhotoSettings.FlashMode = photoOutput.SupportedFlashModes.Contains(flashMode) ? flashMode : photoOutput.SupportedFlashModes.First();

		if (!TryConfigureAVCaptureConnection(photoOutput, out var errorMessage))
		{
			Trace.TraceWarning(errorMessage);
		}

		var wrapper = new AVCapturePhotoCaptureDelegateWrapper();

		photoOutput.CapturePhoto(capturePhotoSettings, wrapper);

		var result = await wrapper.Task.WaitAsync(token);
		if (result.Error is not null)
		{
			var failureReason = result.Error.LocalizedDescription;
			if (!string.IsNullOrEmpty(result.Error.LocalizedFailureReason))
			{
				failureReason = $"{failureReason} - {result.Error.LocalizedFailureReason}";
			}

			cameraView.OnMediaCapturedFailed(failureReason);
			return;
		}

		Stream? imageData;
		try
		{
			imageData = result.Photo.FileDataRepresentation?.AsStream();
		}
		catch (Exception e)
		{
			// possible exception: ObjCException NSInvalidArgumentException NSAllocateMemoryPages(...) failed in AVCapturePhoto.get_FileDataRepresentation()
			cameraView.OnMediaCapturedFailed($"Unable to retrieve the file data representation from the captured result: {e.Message}");
			return;
		}

		if (imageData is null)
		{
			cameraView.OnMediaCapturedFailed("Unable to retrieve the file data representation from the captured result.");
		}
		else
		{
			cameraView.OnMediaCaptured(imageData);
		}
	}

	static AVCaptureVideoOrientation GetVideoOrientationFromAccelerometer(double x, double y)
	{
		// Absolute values help determine which axis is dominant
		if (Math.Abs(y) >= Math.Abs(x))
		{
			return y > 0 ? AVCaptureVideoOrientation.PortraitUpsideDown : AVCaptureVideoOrientation.Portrait;
		}
		else
		{
			// x > 0 is LandscapeRight for device, which is LandscapeLeft for Video
			return x > 0 ? AVCaptureVideoOrientation.LandscapeLeft : AVCaptureVideoOrientation.LandscapeRight;
		}
	}

	static AVCaptureVideoOrientation GetVideoOrientation()
	{
		IEnumerable<UIScene> scenes = UIApplication.SharedApplication.ConnectedScenes;

		UIInterfaceOrientation interfaceOrientation;
		if (!(OperatingSystem.IsMacCatalystVersionAtLeast(26) || OperatingSystem.IsIOSVersionAtLeast(26)))
		{
			interfaceOrientation = scenes.FirstOrDefault() is UIWindowScene windowScene
				? windowScene.InterfaceOrientation
				: UIApplication.SharedApplication.StatusBarOrientation;
		}
		else
		{
			interfaceOrientation = scenes.FirstOrDefault() is UIWindowScene windowScene
				? windowScene.EffectiveGeometry.InterfaceOrientation
				: UIApplication.SharedApplication.StatusBarOrientation;
		}

		return interfaceOrientation switch
		{
			UIInterfaceOrientation.Portrait => AVCaptureVideoOrientation.Portrait,
			UIInterfaceOrientation.PortraitUpsideDown => AVCaptureVideoOrientation.PortraitUpsideDown,
			UIInterfaceOrientation.LandscapeRight => AVCaptureVideoOrientation.LandscapeRight,
			UIInterfaceOrientation.LandscapeLeft => AVCaptureVideoOrientation.LandscapeLeft,
			_ => AVCaptureVideoOrientation.Portrait
		};
	}

	/// Builds an ordered list of session presets from highest to lowest quality,
	/// capped by <see cref="VideoRecordingOptions.MaxResolutionLines"/>.
	/// Mirrors the Android <c>CreateQualitySelector</c> logic.
	static IEnumerable<NSString> CreateSessionPresets(VideoRecordingOptions options)
	{
		var lines = options.MaxResolutionLines;
		if (lines <= 0)
		{
			lines = int.MaxValue;
		}

		if (lines >= 2160)
		{
			yield return AVCaptureSession.Preset3840x2160;
		}
		if (lines >= 1080)
		{
			yield return AVCaptureSession.Preset1920x1080;
		}
		if (lines >= 720)
		{
			yield return AVCaptureSession.Preset1280x720;
		}
		if (lines >= 480)
		{
			yield return AVCaptureSession.PresetMedium;
		}
	}

	/// Maps a <see cref="VideoRecordingOptions.VideoCodec"/> string to an <see cref="AVVideoCodecType"/>
	/// and applies it to the given <paramref name="output"/> via <c>SetOutputSettings</c>.
	static void ApplyVideoCodec(AVCaptureMovieFileOutput output, string videoCodec)
	{
		var codecType = videoCodec switch
		{
			VideoRecordingOptions.H264VideoCodec => AVVideoCodecType.H264,
			VideoRecordingOptions.HevcVideoCodec => AVVideoCodecType.Hevc,
			_ => (AVVideoCodecType?)null
		};

		if (codecType is null)
		{
			Trace.TraceWarning($"Unknown video codec '{videoCodec}'; the system default will be used.");
			return;
		}

		if (AVMediaTypes.Video.GetConstant() is not NSString avMediaTypeVideo)
		{
			return;
		}

		if (output.ConnectionFromMediaType(avMediaTypeVideo) is not AVCaptureConnection connection)
		{
			return;
		}

		if (codecType.Value.GetConstant() is not NSString codecConstant)
		{
			return;
		}

		var settings = new NSDictionary<NSString, NSObject>(AVVideo.CodecKey, codecConstant);
		output.SetOutputSettings(settings, connection);
	}

	bool TryConfigureAVCaptureConnection(in AVCaptureOutput captureOutput, [NotNullWhen(false)] out string? errorMessage)
	{
		errorMessage = null;

		if (AVMediaTypes.Video.GetConstant() is not NSString avMediaTypeVideo)
		{
			errorMessage = "Unable to determine video format.";
			return false;
		}

		if (captureOutput.ConnectionFromMediaType(avMediaTypeVideo) is not AVCaptureConnection captureConnection)
		{
			errorMessage = "Unable to determine video connection from media type.";
			return false;
		}

		// use AVCaptureDeviceRotationCoordinator to set captured photo and video orientation on iOS 17+
		if (UIDevice.CurrentDevice.CheckSystemVersion(17, 0))
		{
			if (rotationCoordinator is not null)
			{
				captureConnection.VideoRotationAngle = rotationCoordinator.VideoRotationAngleForHorizonLevelCapture;
			}
		}
		// use CMMotionManager to set captured photo and video orientation on iOS 16 and lower
		else
		{
			var data = motionManager?.AccelerometerData;
			if (data is not null)
			{
				var orientation = GetVideoOrientationFromAccelerometer(data.Acceleration.X, data.Acceleration.Y);
				captureConnection.VideoOrientation = orientation;
			}
		}

		if (captureConnection.SupportsVideoMirroring)
		{
			captureConnection.AutomaticallyAdjustsVideoMirroring = false;
			captureConnection.VideoMirrored = cameraView.SelectedCamera?.Position is CameraPosition.Front;
		}

		return true;
	}

	void UpdateVideoOrientation()
	{
		if (UIDevice.CurrentDevice.CheckSystemVersion(17, 0) && rotationCoordinator is not null)
		{
			// PreviewLayerVideoRotationAngleForHorizonLevelPreview is not yet bound in this SDK version;
			// VideoRotationAngleForHorizonLevelCapture produces the same angle for the preview layer.
			videoRotationAngle = rotationCoordinator.VideoRotationAngleForHorizonLevelCapture;
			previewView?.UpdatePreviewVideoOrientation(videoRotationAngle);
		}
		else
		{
			videoOrientation = GetVideoOrientation();
			previewView?.UpdatePreviewVideoOrientation(videoOrientation);
		}
	}

	IEnumerable<AVCaptureDeviceFormat> GetPhotoCompatibleFormats(IEnumerable<AVCaptureDeviceFormat> formats)
	{
		if (photoOutput is not null)
		{
			var photoPixelFormats = photoOutput.GetSupportedPhotoPixelFormatTypesForFileType(nameof(AVFileTypes.Jpeg));
			return formats.Where(format => photoPixelFormats.Contains((NSNumber)format.FormatDescription.MediaSubType));
		}

		return formats;
	}

	static bool MatchesResolution(AVCaptureDeviceFormat format, Size resolution)
	{
		var dimensions = ((CMVideoFormatDescription)format.FormatDescription).Dimensions;
		return dimensions.Width <= resolution.Width
			   && dimensions.Height <= resolution.Height;
	}

	sealed class AVCapturePhotoCaptureDelegateWrapper : AVCapturePhotoCaptureDelegate
	{
		readonly TaskCompletionSource<CapturePhotoResult> taskCompletionSource = new();

		public Task<CapturePhotoResult> Task =>
			taskCompletionSource.Task;

		public override void DidFinishProcessingPhoto(AVCapturePhotoOutput output, AVCapturePhoto photo, NSError? error)
		{
			taskCompletionSource.TrySetResult(new()
			{
				Output = output,
				Photo = photo,
				Error = error
			});
		}
	}

	sealed record CapturePhotoResult
	{
		public required AVCapturePhotoOutput Output { get; init; }

		public required AVCapturePhoto Photo { get; init; }

		public NSError? Error { get; init; }
	}

	sealed class PreviewView : NativePlatformCameraPreviewView
	{
		public PreviewView()
		{
			PreviewLayer.VideoGravity = AVLayerVideoGravity.ResizeAspectFill;
		}

		public AVCaptureSession? Session
		{
			get => PreviewLayer.Session;
			set => PreviewLayer.Session = value;
		}

		AVCaptureVideoPreviewLayer PreviewLayer => (AVCaptureVideoPreviewLayer)Layer;

		// Tracks the last applied rotation so LayoutSubviews can re-apply it without a coordinator reference.
		bool hasRotationAngle;
		nfloat lastRotationAngle;
		AVCaptureVideoOrientation lastVideoOrientation;

		[Export("layerClass")]
		public static Class GetLayerClass()
		{
			return new Class(typeof(AVCaptureVideoPreviewLayer));
		}

		public override void LayoutSubviews()
		{
			base.LayoutSubviews();
			if (hasRotationAngle)
			{
				ApplyRotationAngle(lastRotationAngle);
			}
			else
			{
				ApplyVideoOrientation(lastVideoOrientation != default ? lastVideoOrientation : GetVideoOrientation());
			}
		}

		/// <summary>iOS 17+ path — uses <see cref="AVCaptureConnection.VideoRotationAngle"/>.</summary>
		public void UpdatePreviewVideoOrientation(nfloat angle)
		{
			Debug.WriteLine($">> UpdatePreviewVideoOrientation(angle={angle})");
			hasRotationAngle = true;
			lastRotationAngle = angle;
			ApplyRotationAngle(angle);
		}

		/// <summary>iOS &lt;17 path — uses the deprecated <see cref="AVCaptureConnection.VideoOrientation"/>.</summary>
		public void UpdatePreviewVideoOrientation(AVCaptureVideoOrientation videoOrientation)
		{
			Debug.WriteLine($">> UpdatePreviewVideoOrientation({videoOrientation})");
			hasRotationAngle = false;
			lastVideoOrientation = videoOrientation;
			ApplyVideoOrientation(videoOrientation);
		}

		void ApplyRotationAngle(nfloat angle)
		{
			if (PreviewLayer.Connection is not null)
			{
				PreviewLayer.Connection.VideoRotationAngle = angle;
			}
		}

		void ApplyVideoOrientation(AVCaptureVideoOrientation videoOrientation)
		{
			if (PreviewLayer.Connection is not null)
			{
				PreviewLayer.Connection.VideoOrientation = videoOrientation;
			}
		}
	}
}

class AVCaptureMovieFileOutputRecordingDelegate(TaskCompletionSource taskCompletionSource) : AVCaptureFileOutputRecordingDelegate
{
	public override void FinishedRecording(AVCaptureFileOutput captureOutput, NSUrl outputFileUrl, NSObject[] connections, NSError? error)
	{
		taskCompletionSource.SetResult();
	}
}