using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using System.IO;
using System.Threading;
using System.Timers;
using Microsoft.Win32;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using TestingPlatformLibrary.FunctionGeneratorAPI;
using TestingPlatformLibrary.OscilloscopeAPI;

namespace UnifiedTestSuiteApp
{
    public partial class MainWindow : Window
    {
        // Draws the waveform stored in currentWaveform on the app's graph canvas
        private void FG_DrawWaveformGraph()
        {
            FG_DrawWaveformGraph(currentWaveform);
        }

        // Draws the waveform contained in the given WaveformFile on the app's graph canvas
        private void FG_DrawWaveformGraph(WaveformFile wave)
        {
            int length;
            FGWaveformGraphDataLine.Points.Clear();
            if (wave == null || wave.FileName == null)  // if there's nothing actually saved in the current waveform, just draw the reference
                                                        // line for 0V and then return
            {
                FGWaveformGraphZeroLine.Points.Clear();
                FGWaveformGraphZeroLine.Points.Add(new DataPoint(0, 0));
                FGWaveformGraphZeroLine.Points.Add(new DataPoint(FGWaveformPlot.ActualWidth, 0));
                FGWaveformPlot.Model.InvalidatePlot(true);
                return;
            }
            if (wave.Voltages.Length > 1000)
            {
                length = 1000;
            }
            else
            {
                length = wave.Voltages.Length;
            }
            FGWaveformGraphZeroLine.Points.Clear();
            FGWaveformGraphZeroLine.Points.Add(new DataPoint(0, 0));
            FGWaveformGraphZeroLine.Points.Add(new DataPoint(length, 0));
            for (int i = 0; i < length; i++)
            {

                FGWaveformGraphDataLine.Points.Add(new DataPoint(i, wave.Voltages[i]));

            }
            FGWaveformPlot.Model.InvalidatePlot(true);
            if (openingFile)  // if we're in file opening mode
            {
                WaveformList.IsEnabled = true; // it's best just to wait until the graph is drawn.
            }
        }

        private void FG_Button_Click_OpenWaveform(object sender, RoutedEventArgs e)
        {
            openingFile = true;
            WaveformList.IsEnabled = false;  // if the user attempts to click a memory location to save the waveform before it's actually
            // parsed and saved, the UI gets into a weird state where it is unable to actually 
            OpenFileDialog dlg = new OpenFileDialog
            {
                DefaultExt = ".txt",
                //|*.jpeg|PNG Files (*.png)|*.png|JPG Files (*.jpg)|*.jpg|GIF Files (*.gif)|*.gif
                Filter = "txt files (*.txt)|*.txt|All files (*.*)|*.*"  // filter shows txt files and other files if selected
            };  // open a file dialog
            bool? result = dlg.ShowDialog();
            if (result == true)
            {
                string filePath = dlg.FileName;  // get the name of the file
                cancelFileOpen.Visibility = Visibility.Visible;  // show the button for canceling file upload
                OffsetErrorLabel.Visibility = Visibility.Hidden;  // hide the error label if it's showing
                AmplitudeErrorLabel.Visibility = Visibility.Hidden;  // hide the amplitude error label too
                FG_ParseFileHandler(filePath);
                WaveformSaveInstructionLabel.Visibility = Visibility.Visible;  // show the instructions on how to save waveform
                //EditWaveformParameterCheckbox.IsEnabled = true;  // enable the edit waveform parameter checkbox

            }
        }

        private void FG_Button_Click_PlayWaveform(object sender, RoutedEventArgs e)
        {
            channelsPlaying.Add(functionGeneratorChannelInFocus);
            // we only need to worry about what mode the function generator is in when the user hits play.
            // since this command is instant, we don't have to keep track of the current mode with a seperate thread.
            if (calibration)
            {
                fg.SetWaveformType(WaveformType.SINE, functionGeneratorChannelInFocus);  // set to sine wave mode for calibration waveforms
            }
            else
            {
                fg.SetWaveformType(WaveformType.ARB, functionGeneratorChannelInFocus);  // set to arbitrary wave mode for loaded waveforms.
                fg.SetSampleRate(currentWaveform.SampleRate, functionGeneratorChannelInFocus);  // set the sample rate. (and set Arb mode to TrueArb)
            }
            fg.SetOutputOn(functionGeneratorChannelInFocus);
            PlayWaveform.Content = "Restart Waveform";  // when the play waveform button is clicked again it restarts the waveform
            // from the beginning
        }

        private void FG_Button_Click_EStop(object sender, RoutedEventArgs e)
        {
            fg.SetAllOutputsOff();  // turn off all outputs
            channelsPlaying.Clear();
            calibration = false;
            PlayWaveform.Content = "Play Waveform";
            channelsPlaying.Remove(functionGeneratorChannelInFocus);
            if (calibration)
            {
                PlayWaveform.IsEnabled = true;
                CalibrationButton.IsEnabled = true;
                WaveformList.IsEnabled = true;
                calibration = false;
                fg.SetWaveformType(WaveformType.ARB, functionGeneratorChannelInFocus);  // after the user stops calibration switch back to ARB mode
            }
        }

        private void FG_Button_Click_StopWaveform(object sender, RoutedEventArgs e)
        {
            fg.SetOutputOff(functionGeneratorChannelInFocus);  // turn off the current channel's output
            channelsPlaying.Remove(functionGeneratorChannelInFocus);
            PlayWaveform.Content = "Play Waveform";
            if (calibration)
            {
                CalibrationButton.IsEnabled = true;
                WaveformList.IsEnabled = true;
                calibration = false;
                fg.SetWaveformType(WaveformType.ARB, functionGeneratorChannelInFocus);  // after the user stops calibration switch back to ARB mode
            }
        }

        private void FG_ParseFileHandler(string filePath)
        {
            Thread t = new Thread(() => FG_ParseFile(filePath));  // spin off a new thread for file parsing
            t.Start();
        }

        private void FG_ParseFile(string filePath)
        {

            double sampleRate = 844;  // default samplerate
            string fileName = System.IO.Path.GetFileName(filePath);
            IEnumerable<string> fileLines = File.ReadLines(filePath);
            if (fileLines.First().StartsWith("samplerate="))
            {
                sampleRate = double.Parse(fileLines.First().Substring(11));  // parse the sampleRate
                fileLines = fileLines.Skip(1);  // and then we have to skip the first one.
            }
            double[] voltageArray = fileLines.AsParallel().AsOrdered().Select(line => double.Parse(line)).ToArray();
            // parse to an IEnumerable of doubles, using Parallel processing, but preserving the order
            if (voltageArray.AsParallel().Max() - voltageArray.AsParallel().Min() > maximumAllowedAmplitude)  // amplitude is too high
            {
                // signal the UI thread to display the error/warning
                Application.Current.Dispatcher.Invoke(() => {
                    AmplitudeErrorLabel.Visibility = Visibility.Visible;
                });
                double absMax = Math.Max(Math.Abs(voltageArray.AsParallel().Max()), Math.Abs(voltageArray.AsParallel().Min()));
                // get the maximum absolute value from the waveform.
                // now we can't just use the ScaleWaveform() function for this because then it would set the original 
                // values to ones that were beyond the max/min. 
                double scalingFactor = absMax / (maximumAllowedAmplitude / 2);  // get the scaling factor
                Parallel.For(0, voltageArray.Length, (i, state) =>
                {
                    voltageArray[i] = voltageArray[i] / scalingFactor;  // then we multiply every value in the waveform by the scaling factor.
                });
                // and then we just move on

            }
            int returnedValue = FG_RemoveDCOffset(voltageArray);  // remove the DC offset from the waveform if there is one.
            if (returnedValue == -1)  // then there was an error removing the DC offset
            {
                // signal the UI thread to show the DC offset removal error and clean up the opening file process
                Application.Current.Dispatcher.Invoke(() => {
                    OffsetErrorLabel.Visibility = Visibility.Visible;
                    cancelFileOpen.Visibility = Visibility.Hidden;  // hide the cancel file open button
                    WaveformSaveInstructionLabel.Visibility = Visibility.Hidden;  // hide the save file instructions
                    WaveformList.IsEnabled = true; // also reenable the list
                });
                openingFile = false;  // set opening file to false
                return;  // then just give up without actually saving the waveform or anything.
            }

            // for safety reasons, we will only be putting in a maximum amplitude of 1Vpp into our bucket.
            // However, checking for this does make the code less flexible, so here are the lines of code that cause this.
            // 1Vpp checking code:
            // should we check this before or after DC offset removal. Before is faster, but after is better. So after.

            // okay so we need to automatically scale the amplitude down to fit the maximum allowed amplitude.



            WaveformFile fileStruct = new WaveformFile(sampleRate, voltageArray.Length, fileName, voltageArray, filePath);

            // create a new WaveformFileStruct to contain the info about this waveform file

            currentWaveform = fileStruct;  // then set the waveform data for the currently selected memory address
            Application.Current.Dispatcher.Invoke(FG_DrawWaveformGraph);  // attempt to signal the UI thread to update the graph as soon
            // as we are done doing the parsing

        }

        /// <summary>
        /// This function removes the DC offset from the waveform given, by taking the average and subtracting it from all points
        /// </summary>
        /// <param name="voltageArray">The array of voltages</param>
        /// <returns>0 if the operation succeeded, -1 if there was an error</returns>
        private int FG_RemoveDCOffset(double[] voltageArray)
        {
            double average = voltageArray.AsParallel().Sum() / voltageArray.Length; // get the average voltage of the waveform in the array.
            if (Math.Abs(average) < 0.0001)  // if the DC offset is really that small (less than 1mV)
                                             // then there's no need to worry about it
                                             // and possibly introduce artifacts into the waveform with floating point errors
            {
                return 0;
            }
            if ((voltageArray.AsParallel().Max() - average) > maximumAllowedAmplitude / 2)  // if after we remove the offset
                                                                                            // stuff would get truncated or crash the function generator, we have a problem.
            {
                return -1;  // DO something here to let the user know there was a problem
            }
            if ((voltageArray.AsParallel().Min() - average) < -1 * (maximumAllowedAmplitude / 2))  // if after we remove the offset
                                                                                                   // stuff would get truncated or crash the function generator, we have a problem.
            {
                return -1;  // do something here to let the user know there was a problem
            }
            // POSSIBLY ALERT USER ABOUT A DC OFFSET IN THE WAVEFORM
            // this parallel for thing is amazing
            Parallel.For(0, voltageArray.Length, (i, state) =>
            {

                voltageArray[i] -= average;  // we subtract the average from every voltage in the waveform, therefore removing the 
                                             // DC offset

            });
            return 0;
        }

        private void FG_Button_Click_Calibrate(object sender, RoutedEventArgs e)
        {
            calibration = true;
            fg.CalibrateWaveform(functionGeneratorChannelInFocus);
            CalibrationButton.IsEnabled = false;
            WaveformList.IsEnabled = false;

        }

        // a double click on one of the memory locations in the list
        private void FG_WaveformList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            memoryLocationSelected = (string)WaveformList.SelectedItem;  // the data stored in WaveformList will always be strings            
            WaveformFile current = fileDataMap[memoryLocationSelected];  // get the waveformFileStruct from the selected memory location
            OffsetErrorLabel.Visibility = Visibility.Hidden;  // hide the offset error label if it's showing
            AmplitudeErrorLabel.Visibility = Visibility.Hidden;  // hide the amplitude error one too
            if (openingFile)
            { // if we are currently in opening file mode
                if (current.FileName == null)  // if the memory location that was clicked on is empty, just save the data there.
                {
                    fileDataMap[memoryLocationSelected] = currentWaveform;  // save the data
                    openingFile = false;  // set opening file to false
                    WaveformSaveInstructionLabel.Visibility = Visibility.Hidden;  // hide the instruction label
                    cancelFileOpen.Visibility = Visibility.Hidden;  // hide the cancel button again.
                    WaveformName.Content = currentWaveform.FileName;  // show the displayed name
                    WaveformMemoryLocation.Content = memoryLocationSelected;  // show the memory location
                    WaveformSampleRate.Text = currentWaveform.SampleRate.ToString();  // show the sample rate
                    WaveformAmplitudeScaleFactor.Text = currentWaveform.ScaleFactor.ToString();  // show the scaling factor
                    EditWaveformParameterCheckbox.IsEnabled = true;  // enable the edit waveform parameter checkbox
                    ListBoxItem item = WaveformList.ItemContainerGenerator.ContainerFromItem(WaveformList.SelectedItem) as ListBoxItem;
                    item.Background = Brushes.Gold;
                    // set the color to gold to signal that the waveform is saved but
                    // not uploaded
                    if (!(uploading || loading))
                    {
                        UploadWaveform.IsEnabled = true;  // enable the upload button
                    }
                    FG_DrawWaveformGraph();  // draw the graph

                    return;  // then just return.

                }
                else // if the memory location that was clicked on isn't empty, ask the user if they are okay with overwriting the data that
                     // is already there
                {
                    if (MessageBox.Show("Overwrite Waveform in " + memoryLocationSelected, "Overwrite Waveform?",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    {
                        fileDataMap[memoryLocationSelected] = currentWaveform; // they said it was okay to overwrite
                        openingFile = false;  // set opening file to false
                        cancelFileOpen.Visibility = Visibility.Hidden;  // hide the cancel button again.
                        WaveformSaveInstructionLabel.Visibility = Visibility.Hidden;  // hide the instruction label
                        WaveformName.Content = currentWaveform.FileName;  // show the displayed name
                        WaveformMemoryLocation.Content = memoryLocationSelected;  // show the memory location
                        WaveformSampleRate.Text = currentWaveform.SampleRate.ToString();  // show the sample rate
                        WaveformAmplitudeScaleFactor.Text = currentWaveform.ScaleFactor.ToString();  // show the scaling factor
                        FG_DrawWaveformGraph();  // draw the graph
                        if (!(uploading || loading))
                        {
                            UploadWaveform.IsEnabled = true;  // enable the upload button
                        }
                        ListBoxItem item = WaveformList.ItemContainerGenerator.ContainerFromItem(WaveformList.SelectedItem) as ListBoxItem;
                        EditWaveformParameterCheckbox.IsEnabled = true;  // enable the edit waveform parameter checkbox
                        item.Background = Brushes.Gold;  // set the color to gold to signal that the waveform is saved but
                        // not uploaded
                        return;  // then just return.

                    }
                    else
                    {
                        // we don't set opening file to false because the user may have clicked on the wrong waveform or something
                        // the empty else branch is left here as a placeholder.
                        return;  // just return
                    }
                }
            }
            else  // if the user is not opening a file, and just double clicks on the memory location, load the waveform there into current
            {
                current = fileDataMap[memoryLocationSelected];  // reload current value
                                                                // if the waveform selected has set values, display them for the user.
                                                                // (Then also like load the file into the graph but that's for later)
                currentWaveform = current;
                if (current.FileName == null)  // if there isn't any data saved there, keep the original "no data" messages
                {
                    WaveformName.Content = "No data in requested location";
                    WaveformMemoryLocation.Content = memoryLocationSelected;  // show the memory location
                    WaveformSampleRate.Text = "No data in requested location";
                    WaveformAmplitudeScaleFactor.Text = "No data in requested location";  // show the scaling factor
                    EditWaveformParameterCheckbox.IsEnabled = false;  // disable the edit waveform parameter checkbox
                    FG_DrawWaveformGraph();  // draw an empty graph
                    UploadWaveform.IsEnabled = false;  // disable the upload button
                    return;
                }  // if it's not null, just show it's data
                WaveformName.Content = current.FileName;  // show the displayed name
                WaveformMemoryLocation.Content = memoryLocationSelected;  // show the memory location
                WaveformSampleRate.Text = current.SampleRate.ToString();  // show the sample rate
                WaveformAmplitudeScaleFactor.Text = currentWaveform.ScaleFactor.ToString();  // show the scaling factor
                EditWaveformParameterCheckbox.IsEnabled = true;  // enable the edit waveform parameter checkbox
                if (!(uploading || loading))
                {
                    UploadWaveform.IsEnabled = true;  // enable the upload button
                }
                FG_DrawWaveformGraph();  // draw the graph with the data saved in the memory location
            }

        }

        // a single click on one of the memory locations in the list
        private void FG_WaveformList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            OffsetErrorLabel.Visibility = Visibility.Hidden;  // hide the offset error label if it's showing
            AmplitudeErrorLabel.Visibility = Visibility.Hidden; // hide the amplitude one too
            memoryLocationSelected = (string)WaveformList.SelectedItem;  // the data stored in WaveformList will always be strings
            WaveformFile current = fileDataMap[memoryLocationSelected];  // get the waveformFileStruct from the selected memory location

            if (current.FileName == null)  // if the waveform selected still has its default values, just let the user know and then return
            {
                WaveformName.Content = "No data in requested location";
                WaveformMemoryLocation.Content = memoryLocationSelected;  // show the memory location
                WaveformSampleRate.Text = "No data in requested location";
                WaveformAmplitudeScaleFactor.Text = "No data in requested location";  // show the scaling factor

                FG_DrawWaveformGraph(current);  // draw an empty graph, with just the 0V reference line

                UploadWaveform.IsEnabled = false;  // try just disabling the button instead of hiding it
                EditWaveformParameterCheckbox.IsEnabled = false;  // disable the edit waveform parameter checkbox
                LoadWaveformButton.IsEnabled = false;  // if there's nothing in the memory location, we definitely can't load it
                PlayWaveform.IsEnabled = false;  // we definitely can't play it if there's nothing there
                return;
            }
            else
            {
                if (!openingFile)
                {
                    currentWaveform = current;
                }
                // UploadWaveform.IsEnabled = true;  // try just enabling the button instead of showing it
                EditWaveformParameterCheckbox.IsEnabled = true;  // enable the edit waveform parameter checkbox
                if (currentWaveform.IsUploaded)  // if the waveform in the memory location is uploaded to the generator
                {
                    if (!(uploading || loading))  // as long as no operation is in progress
                    {
                        LoadWaveformButton.IsEnabled = true;  // we can enable the button to load the waveform into active memory
                        UploadWaveform.IsEnabled = true;
                    }
                    if (currentWaveform.ChannelsLoadedTo.Contains(functionGeneratorChannelInFocus))  // if the waveform is loaded into the 
                                                                                                     //selected channel's active memory. 
                    {
                        if (!loading)  // if we're not currently loading a waveform
                        {
                            PlayWaveform.IsEnabled = true;  // enable the playwaveform button
                        }
                    }
                    else
                    {
                        PlayWaveform.IsEnabled = false;  // just in case
                    }
                }
                else
                {
                    if (!(uploading || loading))
                    {
                        UploadWaveform.IsEnabled = true;
                    }
                    LoadWaveformButton.IsEnabled = false;  // just in case
                    PlayWaveform.IsEnabled = false;
                }
                // if the waveform selected has set values, display them for the user.
                WaveformName.Content = current.FileName;  // show the displayed name
                WaveformMemoryLocation.Content = memoryLocationSelected;  // show the memory location
                WaveformSampleRate.Text = current.SampleRate.ToString();  // show the sample rate
                WaveformAmplitudeScaleFactor.Text = current.ScaleFactor.ToString();  // show the scaling factor
                FG_DrawWaveformGraph(current);  // draw the waveform on the app's graph
            }
        }

        private void FG_Button_Click_CancelFileOpen(object sender, RoutedEventArgs e)
        {
            openingFile = false;  // set file opening flag to false
            cancelFileOpen.Visibility = Visibility.Hidden;  // and then hide the button
            WaveformList.IsEnabled = true;  // fix that one bug we found during the usability study 
            WaveformSaveInstructionLabel.Visibility = Visibility.Hidden;  // hide the instruction label
            FG_DrawWaveformGraph(null);  // draw with a null WaveformFile to clear the graph after the user clicked cancel
        }

        private void FG_ChannelChanged()
        {
            if (currentWaveform.ChannelsLoadedTo.Contains(functionGeneratorChannelInFocus))
            // if we switch the channel and the current waveform is loaded to the channel we switched to
            {
                if (!loading)
                {
                    PlayWaveform.IsEnabled = true; // enable the button to play the waveform
                }
            }
            else  // if the opposite happened, where the current waveform is not loaded to the channel we switched to, disable the playwaveform
                  // button
            {
                PlayWaveform.IsEnabled = false;
            }
        }

        private void FG_UploadWaveform_Click(object sender, RoutedEventArgs e)
        {
            uploading = true;  // we can block other UI things from happening. i.e. switching to channel 2 and then back could in some
                               // cases re-activate disabled buttons, and then allow the user to do strange things.
            WaveformUploadMessage.Visibility = Visibility.Visible;  // show the "uploading waveform please wait" message
            // UPLOADING WILL BE DONE IN A SEPERATE THREAD
            WaveformFile data = currentWaveform;
            EditWaveformParameterCheckbox.IsEnabled = false;  // disable the edit waveform parameter checkbox
            currentWaveform.ChannelsLoadedTo.Clear();  // when a new waveform is uploaded, it won't be loaded to any channel
            PlayWaveform.IsEnabled = false;  // we know that this is false because we've cleared everything. This just saves us the trouble
                                             // of forcing an update.
            LoadWaveformButton.IsEnabled = false;
            // these are all non-channel specific, however should probably 
            // stop the user from loading a waveform while the waveform is uploading
            // that could result in the user loading the previous waveform stored in that location, and then overwriting it with the 
            // parameters of the new one, while keeping the waveform the same. This could result in a DC offset and H2 production.

            UploadWaveform.IsEnabled = false;  // disable the upload waveform button. If the user spam clicks it, then we have a problem
            double[] voltages = data.Voltages;
            double SampleRate = data.SampleRate;
            string memLocation = string.Copy((string)WaveformList.SelectedItem);
            // on button click, upload the waveform to the function generator
            //ThreadPool.QueueUserWorkItem(ThreadProc);
            ThreadPool.QueueUserWorkItem(lamdaThing => {
                try
                {  // again we can't use lowLevel and highLevel here because of the way that amplitude scaling works.
                    // if all this parallelism causes CPU bottleneck I could store the scaled min and max as well, or just
                    // have it multiply the original high/low levels by the scaling factor.
                    // actually that's not a bad plan.
                    fg.UploadWaveformData(voltages, SampleRate, (data.ScaleFactor * (data.HighLevel + data.LowLevel)) / 2, 0, memLocation);
                }
                finally
                {
                    FG_WaveformUploadedCallback();
                }
            });
            ListBoxItem item = WaveformList.ItemContainerGenerator.ContainerFromItem(WaveformList.SelectedItem) as ListBoxItem;
            item.Background = Brushes.Green;  // set the background so the user knows that the waveform has been uploaded
            currentWaveform.IsUploaded = true;  // stuff uses this flag
        }

        private void FG_WaveformUploadedCallback()
        {
            // signal the UI thread to update the following UI elements
            Application.Current.Dispatcher.Invoke(() => {
                memoryLocationSelected = (string)WaveformList.SelectedItem;  // the data stored in WaveformList will always be strings    
                WaveformFile temp = fileDataMap[memoryLocationSelected];  // get the waveformFileStruct from the selected memory location
                if (temp.IsUploaded)  // this stops the load button from enabling if the user switched waveforms during the upload process
                {
                    LoadWaveformButton.IsEnabled = true;  // only once the uploading is complete do 
                }
                // we re-enable the button to load the waveform
                UploadWaveform.IsEnabled = true;  // and the button to upload another one
                WaveformUploadMessage.Visibility = Visibility.Hidden;  // hide the "uploading waveform please wait" message
                EditWaveformParameterCheckbox.IsEnabled = true;  // enable the edit waveform parameter checkbox
            });
            uploading = false;  // turn off the uploading flag
        }

        private void FG_LoadWaveformButton_Click(object sender, RoutedEventArgs e)
        {
            //loading = true;  // enable the loading flag
            PlayWaveform.IsEnabled = false;  // gray out the play button until the Loading is complete.
            LoadWaveformButton.IsEnabled = false;  // gray out the load button until this is done, if the user spam clicks it,
            UploadWaveform.IsEnabled = false;  // disable the upload waveform button while loading a waveform
                                               // there will likely be issues.
                                               // if the user switches channels to play a waveform that is 
            string memLocation = string.Copy((string)WaveformList.SelectedItem);
            WaveformLoadMessage.Visibility = Visibility.Visible;  // hide the "waveform loading please wait" message
            ThreadPool.QueueUserWorkItem(lamdaThing => {
                try
                {
                    fg.LoadWaveform(memLocation, functionGeneratorChannelInFocus);
                }
                finally
                {
                    // it seems like the function generator signals operation completion before it's actually done.
                    // how do we do anything about that though?
                    FG_LoadWaveformCallback();  // when the loading is complete actually activate the callback function to
                                                // enable the buttons. This is because loading can take like 30 seconds at worst, and clicking play
                                                // while one is loading is bad
                }
            });
            // load the waveform using a new thread. This is very very very important for large waveforms as they will block the
            // thread for like 30 sec at maximum.
        }

        private void FG_LoadWaveformCallback()
        {
            loading = false;  // disable the loading flag
            currentWaveform.ChannelsLoadedTo.Add(functionGeneratorChannelInFocus);  // add the current channel to the current waveform's set
                                                                                    // of channels it's loaded to
                                                                                    // signal the UI thread to update the state of the elements
            Application.Current.Dispatcher.Invoke(() =>
            {
                memoryLocationSelected = (string)WaveformList.SelectedItem;  // the data stored in WaveformList will always be strings    
                WaveformFile temp = fileDataMap[memoryLocationSelected];  // get the waveformFileStruct from the selected memory location
                if (temp.IsUploaded)
                {
                    LoadWaveformButton.IsEnabled = true;  // we ungray out the load button once complete
                }
                if (temp.ChannelsLoadedTo.Contains(functionGeneratorChannelInFocus))
                {
                    PlayWaveform.IsEnabled = true;  // The loading is complete, so we ungray out the button
                }
                if (temp.FileName != null)
                {
                    UploadWaveform.IsEnabled = true;  // re-enable the upload button
                }
                WaveformLoadMessage.Visibility = Visibility.Hidden;  // hide the "waveform loading please wait" message
            });

        }

        private void FG_PreviewSampleRateInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !IsTextAllowed(e.Text);
        }

        private static readonly Regex _regex = new Regex("[^0-9.]+"); //regex that matches disallowed text
        private static bool IsTextAllowed(string text)
        {
            return !_regex.IsMatch(text);
        }

        private void FG_SaveWaveformParameters_Click(object sender, RoutedEventArgs e)
        {
            memoryLocationSelected = (string)WaveformList.SelectedItem;  // the data stored in WaveformList will always be strings            
            WaveformFile current = fileDataMap[memoryLocationSelected];  // get the waveformFileStruct from the selected memory location
            double sampleRate = double.Parse(WaveformSampleRate.Text);
            if (sampleRate == 0)  // don't let the user set the sample rate to 0 Hz.
            {
                WaveformSampleRate.Text = current.SampleRate.ToString();  // just have it set back to what is was before if they try to put in 0
            }
            else
            {
                current.SampleRate = sampleRate;  // else set the sample rate to what they put in
            }
            double scaleFactor = double.Parse(WaveformAmplitudeScaleFactor.Text);  // get the user set scale factor by parsing the textbox text
            if (scaleFactor != current.ScaleFactor)  // if the scalefactor parsed from the text box doesn't match the one in the WaveformFile
            {
                // then the user has changed the scale factor
                FG_ScaleAmplitude(current, scaleFactor);
                current.ScaleFactor = scaleFactor;  // and then update the scale factor saved in the WaveformFile
            }
            current.IsUploaded = false;
            ListBoxItem item = WaveformList.ItemContainerGenerator.ContainerFromItem(WaveformList.SelectedItem) as ListBoxItem;
            item.Background = Brushes.Gold;  // set the color back to gold to signal that the waveform is saved but is not uploaded.
            // the user will have to click upload again to see changes.
        }

        private void FG_ScaleAmplitude(WaveformFile fileToScale, double scaleFactor)
        {
            double[] originalVoltages = fileToScale.OriginalVoltages;  // get the unscaled voltage array

            if (scaleFactor.Equals(1))  // for sanity, using the .Equals() method
            {
                fileToScale.Voltages = fileToScale.OriginalVoltages;  // set the voltage reference in the WaveformFile to be the original
                // voltages if the user sets the scale factor to 1.
                FG_DrawWaveformGraph();  // draw the graph, can't forget that
                ScalingErrorLabel.Visibility = Visibility.Hidden;
                return;  // then we're done.
            }
            // better to do this checking before we start processing rather than having the API throw an error after we've done all of the work.
            if (fileToScale.HighLevel * scaleFactor > maximumAllowedAmplitude / 2)
            {

                ScalingErrorLabel.Visibility = Visibility.Visible;  // show the scaling error label
                return;
            }
            if (fileToScale.LowLevel * scaleFactor < -1 * (maximumAllowedAmplitude / 2))
            {
                // DO UI SIGNALING THAT SOMETHING IS WRONG
                ScalingErrorLabel.Visibility = Visibility.Visible;  // show the scaling error labelG
                return;
            }
            double[] scaledVoltages = fileToScale.ScaledVoltages;  // get a reference to the scaled voltages in the WaveformFile
            ScalingErrorLabel.Visibility = Visibility.Hidden;
            if (scaledVoltages == null)  // we only init the scaled voltages array when needed.
            {
                // dealing with the shifting around of references to counteract stuff is easier than trying to get the array inside that
                // WaveformFile to be passed by reference of reference.
                scaledVoltages = new double[originalVoltages.Length];  // it will (and must) always be 
                // the same length as the original Voltage array
                fileToScale.ScaledVoltages = scaledVoltages;  // reference weirdness, pay no mind
            }
            Parallel.For(0, originalVoltages.Length, (i, state) => {
                scaledVoltages[i] = originalVoltages[i] * scaleFactor;  // using a parallel for loop, iterate through each of the voltages and 
                                                                        // multiply it by the scale factor before placing it in the scaled voltage array.
            });
            FG_RemoveDCOffset(scaledVoltages);  // remove any DC offset this process created.
            fileToScale.Voltages = scaledVoltages;  // change the reference to scaled voltages
            FG_DrawWaveformGraph();  // and then we redraw the graph so that the changes to the waveform show up immediately
        }


        private void FG_EditWaveformParameterCheckbox_Checked(object sender, RoutedEventArgs e)
        {
            WaveformSampleRate.IsReadOnly = false;
            WaveformSampleRate.IsEnabled = true;
            SaveWaveformParameters.IsEnabled = true;  // enable the save waveform parameters button
            WaveformAmplitudeScaleFactor.IsReadOnly = false;   // make the scale factor textbox not read only
            WaveformAmplitudeScaleFactor.IsEnabled = true;
        }

        private void FG_EditWaveformParameterCheckbox_UnChecked(object sender, RoutedEventArgs e)
        {
            WaveformSampleRate.IsReadOnly = true;
            SaveWaveformParameters.IsEnabled = false;  // disable the save waveform parameters button
            WaveformSampleRate.IsEnabled = false;  // disable the input
            WaveformAmplitudeScaleFactor.IsEnabled = false;
            WaveformAmplitudeScaleFactor.IsReadOnly = true;   // make the scale factor textbox read only 
            memoryLocationSelected = (string)WaveformList.SelectedItem;  // the data stored in WaveformList will always be strings    
            WaveformFile temp = fileDataMap[memoryLocationSelected];  // get the waveformFileStruct from the selected memory location
            WaveformSampleRate.Text = temp.SampleRate.ToString();
            WaveformAmplitudeScaleFactor.Text = temp.ScaleFactor.ToString();
            if (ScalingErrorLabel.Visibility == Visibility.Visible)  // if there was an error when the user unchecks the box
            {
                WaveformAmplitudeScaleFactor.Text = "1";  // just set everything back to 1
                temp.ScaleFactor = 1;
            }
            ScalingErrorLabel.Visibility = Visibility.Hidden;  // then hide the error

        }

        private void FG_WaveformScaleFactor_TextChanged(object sender, TextChangedEventArgs e)
        {
            // currently do nothing on this event
        }

        private void FG_WaveformSampleRate_TextChanged(object sender, TextChangedEventArgs e)
        {
            // currently do nothing on this event
        }

        class WaveformFile
        {
            public double LowLevel { get; set; }
            public double HighLevel { get; set; }
            public double SampleRate { get; set; }
            public int NumSamples { get; set; }
            public string FileName { get; set; }
            public double[] Voltages { get; set; }
            public double[] OriginalVoltages { get; }
            public double[] ScaledVoltages { get; set; }
            public string FilePath { get; set; }
            public bool IsUploaded { get; set; }
            public double ScaleFactor { get; set; }

            public HashSet<int> ChannelsLoadedTo;  // the set of the channel numbers that this waveform is loaded to

            public WaveformFile(double sampleRate, int numSamples, string fileName, double[] voltages, string filePath)
            {
                SampleRate = sampleRate;
                NumSamples = numSamples;
                FileName = fileName;
                OriginalVoltages = voltages;
                Voltages = OriginalVoltages;  // set the reference of voltages to the original voltages passed in (this is for scaling stuff)
                                              // original voltages is also not mutable. we can't set it.
                ScaledVoltages = null;  // this starts off null so we don't need to allocate another (up to 8 million) double array in memory
                                        // unless we need to.
                                        // Example of the Lazy Initialization design pattern. Shoutout to UW CSE 331 and Dr. Hal Perkins.
                FilePath = filePath;
                ScaleFactor = 1;  // On construction of a new WaveformFile object, the scaling factor of the waveform in it is set at 1.
                ChannelsLoadedTo = new HashSet<int>();  // a set that contains the numbers of each channel that this waveform is loaded to
                IsUploaded = false;  // on creation of new struct, IsUploaded is always false

                // If I can get this to work well it would be nice because it would let us make this UI more flexible, and able to work
                // with function generators with varying numbers of channels, not just two.
                if (voltages == null)  // bit of a hack.
                {
                    LowLevel = 0;
                    HighLevel = 0;
                }
                else
                {
                    LowLevel = voltages.AsParallel().Min();
                    HighLevel = voltages.AsParallel().Max();
                }

            }

            public WaveformFile() : this(0, 0, null, null, null)
            {
                // constructor chaining in C# is weird.
            }
        }
    }
}
