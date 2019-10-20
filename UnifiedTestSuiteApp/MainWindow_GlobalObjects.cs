using System.Collections.Generic;
using System.Windows;
using OxyPlot.Axes;
using OxyPlot.Series;
using System.IO;
using System.Threading;
using TestingPlatformLibrary.FunctionGeneratorAPI;
using TestingPlatformLibrary.OscilloscopeAPI;

namespace UnifiedTestSuiteApp
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private const string appName = "S.A.L.T Application";
        private const int refreshIntervalOneTwoChannels = 200;  // the graph refresh interval in ms for when 1-2 channels are enabled
        private const int refreshIntervalThreePlusChannels = 300;
        private int refreshInterval = refreshIntervalOneTwoChannels;  // just to start. Please don't change this manually without a reason
        // updates when we have 3-4 channels enabled)
        private static System.Timers.Timer refreshTimer;    // gotta make sure there's no ambiguity with the threading timer
        private readonly IOscilloscope scope;               // 
        private TextWriter currentLogFile;  // a little bit gross, but overall actually a fine solution
        private int scopeChannelInFocus;
        private readonly HashSet<int> channelsToDisable;
        private readonly LineSeries[] channelGraphs;
        private LineSeries triggerLine;  // the orange line for the trigger value
        private double previousYScaleFactor;
        private readonly CancellationTokenSource cancelToken;  // to avoid errors on closing
        private readonly LinearAxis[] voltageAxes;  // not using the abstract Axis for the type on purpose, we don't want any non-linear axes here
        private readonly object graphLock = new object();
        private readonly object downloadLock = new object();
        private readonly double[] mappedVoltageScales;
        private readonly double[] mappedTimeScales;
        private readonly int numOscilloscopeChannels;
        private readonly HashSet<int> channelsToDraw;
        private bool showTriggerLine;  // whether we should show the dashed orange trigger line or not
        private bool drawGraph = true;
        private readonly double triggerPositionScaleConstant;
        private readonly double voltageOffsetScaleConstant;
        private readonly double timeOffsetScaleConstant;
        private readonly System.Drawing.Color[] channelColors;
        private readonly IFunctionGenerator fg;
        private readonly LineSeries FGWaveformGraphZeroLine;
        private readonly LineSeries FGWaveformGraphDataLine;
        private bool calibration;
        private int functionGeneratorChannelInFocus;
        private bool openingFile;  // true if the user is opening a file
        private string memoryLocationSelected;
        private readonly HashSet<int> channelsPlaying;
        private readonly double maximumAllowedAmplitude = 1;  // set to -1 to allow amplitudes up to the function generator's maximum
        private bool uploading;
        private bool loading;
        // each memory location is mapped to a WaveformFile that represents the waveform saved there.
        private readonly Dictionary<string, WaveformFile> fileDataMap;  // a map from the function generator's valid memory locations to
        // WaveformFiles that contain data about the waveform stored there
        private WaveformFile currentWaveform;

    }
}
