﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Subjects;
using System.Reflection;
using NLog;
using SkiaSharp;
using Logger = NLog.Logger;
using SKSvg = SkiaSharp.Extended.Svg.SKSvg;

namespace LibDmd.Output.Virtual
{
	/// <summary>
	/// A singleton class handling rasterization of the segment assets.
	///
	/// Rasterization means that we're pre-computing images for each layer with
	/// with the desired effects for a given dimension, so we only need to
	/// stick them on each other instead of recompute them every time.
	///
	/// When the size of the display changes (i.e. the user resizes it), we
	/// repeat the rasterization.
	/// </summary>
	internal class AlphaNumericResources
	{
		/// <summary>
		/// The index of the "unlit" segment
		/// </summary>
		public static int FullSegment = 99;

		/// <summary>
		/// The cache ID of the first cache, which isn't display-specific, so
		/// we don't rasterize the same for all displays.
		/// </summary>
		public static int InitialCache = 99;

		/// <summary>
		/// An observable that returns a value as soon as a given segment type is
		/// loaded, meaning the embedded SVG was loaded into Skia.
		/// </summary>
		public Dictionary<SegmentType, ISubject<Unit>> Loaded = new Dictionary<SegmentType, ISubject<Unit>> {
			{ SegmentType.Alphanumeric, new Subject<Unit>()},
			{ SegmentType.Numeric8, new Subject<Unit>() },
			{ SegmentType.Numeric10, new Subject<Unit>() }
		};

		/// <summary>
		/// Defines how many segments each segment type contains (exclusively the
		/// full, "unlit" segment).
		/// </summary>
		public readonly Dictionary<SegmentType, int> SegmentSize = new Dictionary<SegmentType, int> {
			{ SegmentType.Alphanumeric, 16 },
			{ SegmentType.Numeric8, 8 },
			{ SegmentType.Numeric10, 10 },
		};

		private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

		private static AlphaNumericResources _instance;
		private readonly Assembly _assembly = Assembly.GetExecutingAssembly();

		private readonly Dictionary<RasterCacheKey, SKSurface> _rasterCache = new Dictionary<RasterCacheKey, SKSurface>();
		private readonly Dictionary<SegmentType, RasterizeDimensions> _rasterizedDim = new Dictionary<SegmentType, RasterizeDimensions>();
		private readonly Dictionary<SegmentType, Dictionary<int, SKSvg>> _svgs = new Dictionary<SegmentType, Dictionary<int, SKSvg>> {
			{ SegmentType.Alphanumeric, new Dictionary<int, SKSvg>() },
			{ SegmentType.Numeric8, new Dictionary<int, SKSvg>() },
			{ SegmentType.Numeric10, new Dictionary<int, SKSvg>() }
		};

		/// <summary>
		/// Returns the singleton instance.
		/// </summary>
		public static AlphaNumericResources GetInstance()
		{
			return _instance ?? (_instance = new AlphaNumericResources());
		}

		/// <summary>
		/// Returns a surface of a segment that was previously generated.
		/// </summary>
		///
		/// <remarks>
		/// Note that in order to avoid multiple rasterization for each display
		/// on startup, we initially use only one cache. Then, when displays
		/// get individually resized, each display has its own cache.
		/// </remarks>
		/// <param name="display">Display number</param>
		/// <param name="layer">Which layer</param>
		/// <param name="type">Which segment type</param>
		/// <param name="segment">Which segment</param>
		/// <returns>Rasterized surface of null if rasterization is unavailable</returns>
		public SKSurface GetRasterized(int display, RasterizeLayer layer, SegmentType type, int segment)
		{
			// do we have an individual cache for that display already?
			var displayKey = new RasterCacheKey(display, layer, type, segment);
			if (_rasterCache.ContainsKey(displayKey)) {
				return _rasterCache[displayKey];
			}

			// fallback on initial cache
			var initialKey = new RasterCacheKey(InitialCache, layer, type, segment);
			if (_rasterCache.ContainsKey(initialKey)) {
				return _rasterCache[initialKey];
			}
			return null;
		}

		/// <summary>
		/// Returns the size of the SVG of a given segment type.
		/// </summary>
		/// <param name="type">Segment type</param>
		/// <returns>Size of the SVG</returns>
		public SKRect GetSvgSize(SegmentType type)
		{
			return _svgs[type][FullSegment].Picture.CullRect;
		}

		/// <summary>
		/// Renders each layer on a surface and caches it.
		/// </summary>
		///
		/// <remarks>
		/// We assume that if there is already a surface with the same dimension,
		/// it's the same and we skip rendering.
		///
		/// However, during customization, different settings are applied to the
		/// same dimensions, so we there is an additional flag to force
		/// rasterization.
		/// </remarks>
		/// 
		/// <param name="setting">Display setting</param>
		/// <param name="force">True to force, otherwise it'll skip if dimension already rasterized</param>
		public void Rasterize(DisplaySetting setting, bool force = false)
		{
			if (!force && _rasterizedDim.ContainsKey(setting.SegmentType) && _rasterizedDim[setting.SegmentType].Equals(setting.Dim)) {
				Logger.Info("Already rasterized {0}, aborting.", setting.SegmentType);
				return;
			}

			var source = _svgs[setting.SegmentType];
			var segments = source.Keys.Where(i => i != FullSegment).ToList();

			RasterizeLayer(setting, Virtual.RasterizeLayer.OuterGlow, setting.StyleDefinition.OuterGlow, setting.Style.OuterGlow, segments, setting.StyleDefinition.SkewAngle);
			RasterizeLayer(setting, Virtual.RasterizeLayer.InnerGlow, setting.StyleDefinition.InnerGlow, setting.Style.InnerGlow, segments, setting.StyleDefinition.SkewAngle);
			RasterizeLayer(setting, Virtual.RasterizeLayer.Foreground, setting.StyleDefinition.Foreground, setting.Style.Foreground, segments, setting.StyleDefinition.SkewAngle);
			RasterizeLayer(setting, Virtual.RasterizeLayer.Background, setting.StyleDefinition.Background, setting.Style.Background, new []{ FullSegment }, setting.StyleDefinition.SkewAngle);
			
			_rasterizedDim[setting.SegmentType] = setting.Dim;
			Logger.Info("Rasterization done.");
		}

		/// <summary>
		/// Renders a given layer on a surface and caches it.
		/// </summary>
		/// <param name="setting">Display setting</param>
		/// <param name="layer">Which layer is it we're rendering</param>
		/// <param name="layerStyleDef">Style definition</param>
		/// <param name="layerStyle">Style to render</param>
		/// <param name="segments">Which segments to render</param>
		/// <param name="skewAngle">How much to skew</param>
		public void RasterizeLayer(DisplaySetting setting, RasterizeLayer layer, RasterizeLayerStyleDefinition layerStyleDef, RasterizeLayerStyle layerStyle, IEnumerable<int> segments, float skewAngle)
		{
			var source = _svgs[setting.SegmentType];
			Logger.Info("Rasterizing {0} segments of layer {1} on display {2} segment size = {3}x{4}", setting.SegmentType, layer, setting.Display, setting.Dim.SvgWidth, setting.Dim.SvgHeight);
			using (var paint = new SKPaint()) {
				ApplyFilters(paint, layerStyleDef, layerStyle);
				foreach (var i in segments) {
					var initialKey = new RasterCacheKey(InitialCache, layer, setting.SegmentType, i);
					var cacheKey = new RasterCacheKey(setting.Display, layer, setting.SegmentType, i);
					if (!_rasterCache.ContainsKey(initialKey)) {
						_rasterCache[initialKey] = RasterizeSegment(source[i].Picture, setting.Dim, skewAngle, paint);
					} else {
						if (_rasterCache.ContainsKey(cacheKey)) {
							_rasterCache[cacheKey]?.Dispose();
						}
						_rasterCache[cacheKey] = RasterizeSegment(source[i].Picture, setting.Dim, skewAngle, paint);
					}
				}
			}
		}

		/// <summary>
		/// Cleans up the caches.
		/// </summary>
		public void Clear()
		{
			_rasterCache.Clear();
			_rasterizedDim.Clear();
		}

		private AlphaNumericResources()
		{
			LoadAlphaNumeric();
			LoadNumeric8();
			LoadNumeric10();
			Logger.Info("All SVGs loaded.");
		}

		private SKSurface RasterizeSegment(SKPicture segment, RasterizeDimensions dim, float skewAngle, params SKPaint[] paints)
		{
			if (dim.SvgWidth <= 0 || dim.SvgWidth <= 0) {
				return null;
			}
			var surface = SKSurface.Create(dim.SvgInfo);
			surface.Canvas.Translate(dim.Translate.X, dim.Translate.Y);
			Skew(surface.Canvas, skewAngle, 0);
			foreach (var paint in paints) {
				surface.Canvas.DrawPicture(segment, ref dim.SvgMatrix, paint);
			}
			return surface;
		}

		private static void Skew(SKCanvas canvas, double xDegrees, double yDegrees)
		{
			canvas.Skew((float)Math.Tan(Math.PI * xDegrees / 180), (float)Math.Tan(Math.PI * yDegrees / 180));
		}

		private void LoadAlphaNumeric()
		{
			const string prefix = "LibDmd.Output.Virtual.alphanum.";
			var segmentFileNames = new[] {
				$"{prefix}00-top.svg",
				$"{prefix}01-top-right.svg",
				$"{prefix}02-bottom-right.svg",
				$"{prefix}03-bottom.svg",
				$"{prefix}04-bottom-left.svg",
				$"{prefix}05-top-left.svg",
				$"{prefix}06-middle-left.svg",
				$"{prefix}07-comma.svg",
				$"{prefix}08-diag-top-left.svg",
				$"{prefix}09-center-top.svg",
				$"{prefix}10-diag-top-right.svg",
				$"{prefix}11-middle-right.svg",
				$"{prefix}12-diag-bottom-right.svg",
				$"{prefix}13-center-bottom.svg",
				$"{prefix}14-diag-bottom-left.svg",
				$"{prefix}15-dot.svg",
			};
			Logger.Info("Loading alphanumeric SVGs...");
			Load(segmentFileNames, $"{prefix}full.svg", SegmentType.Alphanumeric);
		}

		private void LoadNumeric8()
		{
			const string prefix = "LibDmd.Output.Virtual.numeric.";
			var segmentFileNames = new[] {
				$"{prefix}00-top.svg",
				$"{prefix}01-top-right.svg",
				$"{prefix}02-bottom-right.svg",
				$"{prefix}03-bottom.svg",
				$"{prefix}04-bottom-left.svg",
				$"{prefix}05-top-left.svg",
				$"{prefix}06-middle.svg",
				$"{prefix}07-comma.svg",
			};
			Logger.Info("Loading numeric (8) SVGs...");
			Load(segmentFileNames, $"{prefix}full.svg", SegmentType.Numeric8);
		}

		private void LoadNumeric10()
		{
			const string prefix = "LibDmd.Output.Virtual.numeric.";
			var segmentFileNames = new[] {
				$"{prefix}00-top.svg",
				$"{prefix}01-top-right.svg",
				$"{prefix}02-bottom-right.svg",
				$"{prefix}03-bottom.svg",
				$"{prefix}04-bottom-left.svg",
				$"{prefix}05-top-left.svg",
				$"{prefix}06-middle.svg",
				$"{prefix}07-comma.svg",
				$"{prefix}08-center-top.svg",
				$"{prefix}09-center-bottom.svg",
			};
			Logger.Info("Loading numeric (10) SVGs...");
			Load(segmentFileNames, $"{prefix}full-10.svg", SegmentType.Numeric10);
		}

		private void Load(string[] fileNames, string pathToFull, SegmentType type)
		{
			for (var i = 0; i < fileNames.Length; i++) {
				var svg = new SKSvg();
				svg.Load(_assembly.GetManifestResourceStream(fileNames[i]));
				_svgs[type].Add(i, svg);
			}
			var full = new SKSvg();
			full.Load(_assembly.GetManifestResourceStream(pathToFull));
			_svgs[type].Add(FullSegment, full);
			Loaded[type].OnNext(Unit.Default);
			Loaded[type] = new BehaviorSubject<Unit>(Unit.Default);
		}

		private void ApplyFilters(SKPaint paint, RasterizeLayerStyleDefinition layerStyleDef, RasterizeLayerStyle layerStyle)
		{
			paint.ColorFilter = SKColorFilter.CreateBlendMode(layerStyleDef.Color, SKBlendMode.SrcIn);
			if (layerStyleDef.IsBlurEnabled && layerStyleDef.IsDilateEnabled) {
				paint.ImageFilter = SKImageFilter.CreateBlur(layerStyle.Blur.X, layerStyle.Blur.Y,
					SKImageFilter.CreateDilate((int)layerStyle.Dilate.X, (int)layerStyle.Dilate.Y));
			} else if (layerStyleDef.IsBlurEnabled) {
				paint.ImageFilter = SKImageFilter.CreateBlur(layerStyle.Blur.X, layerStyle.Blur.Y);
			} else if (layerStyleDef.IsDilateEnabled) {
				paint.ImageFilter = SKImageFilter.CreateDilate((int)layerStyle.Dilate.X, (int)layerStyle.Dilate.Y);
			}

		}
	}

	public enum SegmentType
	{
		Alphanumeric, Numeric8, Numeric10
	}

	public enum RasterizeLayer
	{
		OuterGlow, InnerGlow, Foreground, Background
	}

	internal struct RasterCacheKey
	{
		public readonly int Display;
		public readonly RasterizeLayer Layer;
		public readonly SegmentType Type;
		public readonly int Segment;

		public RasterCacheKey(int display, RasterizeLayer layer, SegmentType type, int segment)
		{
			Display = display;
			Layer = layer;
			Type = type;
			Segment = segment;
		}
	}

}
