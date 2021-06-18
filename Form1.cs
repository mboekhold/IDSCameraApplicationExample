using System;
using System.Drawing;
using System.Windows.Forms;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace IDSCameraApplicationExample
{
    public partial class IDSCameraApplicationExample : Form
    {

        private peak.core.Device device;
        private peak.core.NodeMap nodeMapRemoteDevice;
        private peak.core.DataStream dataStream;

        private Thread acquisitionThread;

        private bool running;
        private int frameCounter = 0;
        private int errorCounter = 0;

        public IDSCameraApplicationExample()
        {
            try
            {
                InitializeComponent();

                peak.Library.Initialize();

                ListDevices();
                OpenDevice();

                acquisitionThread = new Thread(StartAcquisition);
                acquisitionThread.Start();
            }
            catch (Exception e)
            {
                Debug.WriteLine("--- [FormWindow] Exception: " + e.Message);
                MessageBox.Show(this, "Exception", e.Message);
            }

        }

        private void StartAcquisition()
        {
            Debug.WriteLine("--- [AcquisitionWorker] Start Acquisition");
            try
            {
                // Lock critical features to prevent them from changing during acquisition
                nodeMapRemoteDevice.FindNode<peak.core.nodes.IntegerNode>("TLParamsLocked").SetValue(1);

                // Start acquisition
                dataStream.StartAcquisition();
                nodeMapRemoteDevice.FindNode<peak.core.nodes.CommandNode>("AcquisitionStart").Execute();
                nodeMapRemoteDevice.FindNode<peak.core.nodes.CommandNode>("AcquisitionStart").WaitUntilDone();
            }
            catch (Exception e)
            {
                Debug.WriteLine("--- [AcquisitionWorker] Exception: " + e.Message);
                MessageBox.Show(this, "Exception", e.Message);
            }

            running = true;

            while (running)
            {
                try
                {
                    // Get buffer from device's datastream
                    var buffer = dataStream.WaitForFinishedBuffer(1000);

                    // Create IDS peak IPL
                    var iplImg = new peak.ipl.Image((peak.ipl.PixelFormatName)buffer.PixelFormat(), buffer.BasePtr(),
                        buffer.Size(), buffer.Width(), buffer.Height());

                    // Debayering and convert IDS peak IPL Image to RGB8 format
                    iplImg = iplImg.ConvertTo(peak.ipl.PixelFormatName.BGR8);

                    var width = Convert.ToInt32(iplImg.Width());
                    var height = Convert.ToInt32(iplImg.Height());
                    var stride = Convert.ToInt32(iplImg.PixelFormat().CalculateStorageSizeOfPixels(iplImg.Width()));

                    // Queue buffer so that it can be used again 
                    dataStream.QueueBuffer(buffer);

                    var image = new Bitmap(width, height, stride,
                        System.Drawing.Imaging.PixelFormat.Format24bppRgb, iplImg.Data());

                    // Create a deep copy of the Bitmap, so it doesn't use memory of the IDS peak IPL Image.
                    // Warning: Don't use image.Clone(), because it only creates a shallow copy!
                    var imageCopy = new Bitmap(image);

                    // The other images are not needed anymore.
                    image.Dispose();
                    iplImg.Dispose();

                    
                    var previousImage = pictureBox.Image;

                    pictureBox.Image = imageCopy;

                    // Manage memory usage by disposing the previous image
                    if (previousImage != null)
                    {
                        previousImage.Dispose();
                    }

                    frameCounter++;
                    if (counterLabel.InvokeRequired)
                    {
                        counterLabel.BeginInvoke((MethodInvoker)delegate { counterLabel.Text = "Acquired: " + frameCounter + ", errors: " + errorCounter; });
                    }
                }
                catch (Exception e)
                {
                    errorCounter++;
                    Debug.WriteLine("--- [AcquisitionWorker] Exception: " + e.Message);
                }

            }
        }

        public bool OpenDevice()
        {
            Debug.WriteLine("--- [BackEnd] Open device");
            try
            {
                // Create instance of the device manager
                var deviceManager = peak.DeviceManager.Instance();

                // Update the device manager
                deviceManager.Update();

                // Return if no device was found
                if (!deviceManager.Devices().Any())
                {
                    Debug.WriteLine("--- [BackEnd] Error: No device found");
                    MessageBox.Show(this, "Exception", "No device found");
                    return false;
                }

                // Open the first openable device in the device manager's device list
                var deviceCount = deviceManager.Devices().Count();

                for (var i = 0; i < deviceCount; ++i)
                {
                    if (deviceManager.Devices()[i].IsOpenable())
                    {
                        device = deviceManager.Devices()[i].OpenDevice(peak.core.DeviceAccessType.Control);

                        // Stop after the first opened device
                        break;
                    }
                    else if (i == (deviceCount - 1))
                    {
                        Debug.WriteLine("--- [BackEnd] Error: Device could not be openend");
                        MessageBox.Show(this, "Error", "This device could not be opened");
                        return false;
                    }
                }

                if (device != null)
                {
                    // Check if any datastreams are available
                    var dataStreams = device.DataStreams();

                    if (!dataStreams.Any())
                    {
                        Debug.WriteLine("--- [BackEnd] Error: Device has no DataStream");
                        MessageBox.Show(this, "Error", "This device has no DataStream");
                        return false;
                    }

                    // Open standard data stream
                    dataStream = dataStreams[0].OpenDataStream();

                    // Get nodemap of remote device for all accesses to the genicam nodemap tree
                    nodeMapRemoteDevice = device.RemoteDevice().NodeMaps()[0];

                    // To prepare for untriggered continuous image acquisition, load the default user set if available
                    // and wait until execution is finished
                    try
                    {
                        nodeMapRemoteDevice.FindNode<peak.core.nodes.EnumerationNode>("UserSetSelector").SetCurrentEntry("Default");
                        nodeMapRemoteDevice.FindNode<peak.core.nodes.CommandNode>("UserSetLoad").Execute();
                        nodeMapRemoteDevice.FindNode<peak.core.nodes.CommandNode>("UserSetLoad").WaitUntilDone();
                    }
                    catch
                    {
                        // UserSet is not available
                    }

                    // Get the payload size for correct buffer allocation
                    UInt32 payloadSize = Convert.ToUInt32(nodeMapRemoteDevice.FindNode<peak.core.nodes.IntegerNode>("PayloadSize").Value());

                    // Get the minimum number of buffers that must be announced
                    var bufferCountMax = dataStream.NumBuffersAnnouncedMinRequired();

                    // Allocate and announce image buffers and queue them
                    for (var bufferCount = 0; bufferCount < bufferCountMax; ++bufferCount)
                    {
                        var buffer = dataStream.AllocAndAnnounceBuffer(payloadSize, IntPtr.Zero);
                        dataStream.QueueBuffer(buffer);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine("--- [BackEnd] Exception: " + e.Message);
                MessageBox.Show(this, "Exception", e.Message);
                return false;
            }

            return true;
        }

        private void ListDevices()
        {
            var deviceManager = peak.DeviceManager.Instance();

            // Update the device manager
            deviceManager.Update();

            var deviceCount = deviceManager.Devices().Count();

            for (var i = 0; i < deviceCount; ++i)
            {
                listBox1.Items.Add(deviceManager.Devices()[i].DisplayName());
            }
        }
    }
}
