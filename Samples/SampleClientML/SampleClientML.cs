﻿//=============================================================================
// Copyright © 2016 NaturalPoint, Inc. All Rights Reserved.
// 
// This software is provided by the copyright holders and contributors "as is" and
// any express or implied warranties, including, but not limited to, the implied
// warranties of merchantability and fitness for a particular purpose are disclaimed.
// In no event shall NaturalPoint, Inc. or contributors be liable for any direct,
// indirect, incidental, special, exemplary, or consequential damages
// (including, but not limited to, procurement of substitute goods or services;
// loss of use, data, or profits; or business interruption) however caused
// and on any theory of liability, whether in contract, strict liability,
// or tort (including negligence or otherwise) arising in any way out of
// the use of this software, even if advised of the possibility of such damage.
//=============================================================================


using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;

using NatNetML;


/* SampleClientML.cs
 * 
 * This program is a sample console application which uses the managed NatNet assembly (NatNetML.dll) for receiving NatNet data
 * from a tracking server (e.g. Motive) and outputting them in every 200 mocap frames. This is provided mainly for demonstration purposes,
 * and thus, the program is designed at its simpliest approach. The program connects to a server application at a localhost IP address
 * (127.0.0.1) using Multicast connection protocol.
 *  
 * You may integrate this program into your applications if needed. This program is not designed to take account for latency/frame build up
 * when tracking a high number of assets. For more robust and comprehensive use of the NatNet assembly, refer to the provided WinFormSample project.
 * 
 *  Note: The NatNet .NET assembly is derived from the native NatNetLib.dll library, so make sure the NatNetLib.dll is linked to your application
 *        along with the NatNetML.dll file.  
 * 
 *  List of Output Data:
 *  ====================
 *      - Markers Data : Prints out total number of markers reconstructed in the scene.
 *      - Rigid Body Data : Prints out position and orientation data
 *      - Skeleton Data : Prints out only the position of the hip segment
 *      - Force Plate Data : Prints out only the first subsample data per each mocap frame
 * 
 */


namespace SampleClientML
{
    public class DroneData
    {
        public IPEndPoint ep;
        public float lastPosN = 0;
        public float lastPosE = 0;
        public float lastPosD = 0;
        public long lastTime = -1;
        public int lost_count = 0;
        public byte uwb_tag_id = 0;
        public int send_count = 0;
        public DroneData(string ip, int port, byte uwb_tag_id)
        {
            ep = new IPEndPoint(IPAddress.Parse(ip), port);
            this.uwb_tag_id = uwb_tag_id;
        }
    }
    public class SampleClientML
    {
        /*  [NatNet] Network connection configuration    */
        private static NatNetML.NatNetClientML mNatNet;    // The client instance
        private static string mStrLocalIP = "127.0.0.1";   // Local IP address (string)
        private static string mStrServerIP = "127.0.0.1";  // Server IP address (string)
        private static NatNetML.ConnectionType mConnectionType = ConnectionType.Multicast; // Multicast or Unicast mode


        /*  List for saving each of datadescriptors */
        private static List<NatNetML.DataDescriptor> mDataDescriptor = new List<NatNetML.DataDescriptor>(); 

        /*  Lists and Hashtables for saving data descriptions   */
        private static Hashtable mHtSkelRBs = new Hashtable();
        private static List<RigidBody> mRigidBodies = new List<RigidBody>();
        private static List<Skeleton> mSkeletons = new List<Skeleton>();
        private static List<ForcePlate> mForcePlates = new List<ForcePlate>();

        /*  boolean value for detecting change in asset */
        private static bool mAssetChanged = false;

        private static MAVLink.MavlinkParse mavlinkParse = new MAVLink.MavlinkParse();
        private static Socket mavSock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private static Dictionary<string, DroneData> drones = new Dictionary<string, DroneData>(5);
        private static Stopwatch stopwatch;

        static void Main()
        {
            stopwatch = new Stopwatch();
            stopwatch.Start();

            drones.Add("micro_uwb", new DroneData("127.0.0.1", 20000, 0));
            drones.Add("micro_uwb2", new DroneData("127.0.0.1", 20000, 1));
            drones.Add("micro_uwb3", new DroneData("127.0.0.1", 20000, 2));

            Console.WriteLine("SampleClientML managed client application starting...\n");
            /*  [NatNet] Initialize client object and connect to the server  */
            connectToServer();                          // Initialize a NatNetClient object and connect to a server.

            Console.WriteLine("============================ SERVER DESCRIPTOR ================================\n");
            /*  [NatNet] Confirming Server Connection. Instantiate the server descriptor object and obtain the server description. */
            bool connectionConfirmed = fetchServerDescriptor();    // To confirm connection, request server description data

            MAVLink.mavlink_system_time_t cmd = new MAVLink.mavlink_system_time_t();
            cmd.time_boot_ms = 0;
            cmd.time_unix_usec = (ulong)((DateTime.UtcNow - unix_epoch).TotalMilliseconds * 1000);
            byte[] pkt = mavlinkParse.GenerateMAVLinkPacket20(MAVLink.MAVLINK_MSG_ID.SYSTEM_TIME, cmd);
            foreach (KeyValuePair<string, DroneData> drone in drones)
            {
                mavSock.SendTo(pkt, drone.Value.ep);
            }

            if (connectionConfirmed)                         // Once the connection is confirmed.
            {
                Console.WriteLine("============================= DATA DESCRIPTOR =================================\n");
                Console.WriteLine("Now Fetching the Data Descriptor.\n");
                fetchDataDescriptor();                  //Fetch and parse data descriptor

                Console.WriteLine("============================= FRAME OF DATA ===================================\n");
                Console.WriteLine("Now Fetching the Frame Data\n");

                /*  [NatNet] Assigning a event handler function for fetching frame data each time a frame is received   */
                mNatNet.OnFrameReady += new NatNetML.FrameReadyEventHandler(fetchFrameData);
               
                Console.WriteLine("Success: Data Port Connected \n");

                Console.WriteLine("======================== STREAMING IN (PRESS ESC TO EXIT) =====================\n");
            }


            while (!(Console.KeyAvailable && Console.ReadKey().Key == ConsoleKey.Escape))
            {
                // Continuously listening for Frame data
                // Enter ESC to exit

                // Exception handler for updated assets list.
                if (mAssetChanged == true)
                {
                    Console.WriteLine("\n===============================================================================\n");
                    Console.WriteLine("Change in the list of the assets. Refetching the descriptions");

                    /*  Clear out existing lists */
                    mDataDescriptor.Clear();
                    mHtSkelRBs.Clear();
                    mRigidBodies.Clear();
                    mSkeletons.Clear();
                    mForcePlates.Clear();

                    /* [NatNet] Re-fetch the updated list of descriptors  */
                    fetchDataDescriptor();
                    Console.WriteLine("===============================================================================\n");
                    mAssetChanged = false;
                }
            }
            /*  [NatNet] Disabling data handling function   */
            mNatNet.OnFrameReady -= fetchFrameData;

            mavSock.Close();
            stopwatch.Stop();

            /*  Clearing Saved Descriptions */
            mRigidBodies.Clear();
            mSkeletons.Clear();
            mHtSkelRBs.Clear();
            mForcePlates.Clear();
            mNatNet.Disconnect();
        }



        /// <summary>
        /// [NatNet] parseFrameData will be called when a frame of Mocap
        /// data has is received from the server application.
        ///
        /// Note: This callback is on the network service thread, so it is
        /// important to return from this function quickly as possible 
        /// to prevent incoming frames of data from buffering up on the
        /// network socket.
        ///
        /// Note: "data" is a reference structure to the current frame of data.
        /// NatNet re-uses this same instance for each incoming frame, so it should
        /// not be kept (the values contained in "data" will become replaced after
        /// this callback function has exited).
        /// </summary>
        /// <param name="data">The actual frame of mocap data</param>
        /// <param name="client">The NatNet client instance</param>
        static void fetchFrameData(NatNetML.FrameOfMocapData data, NatNetML.NatNetClientML client)
        {

            /*  Exception handler for cases where assets are added or removed.
                Data description is re-obtained in the main function so that contents
                in the frame handler is kept minimal. */
            if (( data.bTrackingModelsChanged == true || data.nRigidBodies != mRigidBodies.Count || data.nSkeletons != mSkeletons.Count || data.nForcePlates != mForcePlates.Count))
            {
                mAssetChanged = true;
            }

            /*  Processing and ouputting frame data every 200th frame.
                This conditional statement is included in order to simplify the program output */
            //if(data.iFrame % 12 == 0) //camera 120 fps, but ardupilot limit data rate to 70ms
            //for parrot bebop2, I can feed it with 14Hz. But for skyviper v2450, 5hz is max. Faster data seems overload its CPU 
            {
                //if (data.bRecording == false)
                //    Console.WriteLine("Frame #{0} Received:", data.iFrame);
                //else if (data.bRecording == true)
                //    Console.WriteLine("[Recording] Frame #{0} Received:", data.iFrame);

                processFrameData(data);
            }
        }

        static DateTime gps_epoch = new DateTime(1980, 1, 6, 0, 0, 0, DateTimeKind.Utc);
        static DateTime unix_epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        static DateTime my_start = DateTime.UtcNow;
        //static uint GPS_LEAPSECONDS_MILLIS = 18000;

        static void processFrameData(NatNetML.FrameOfMocapData data)
        {
            bool data_gogo = true;
            /*  Parsing Rigid Body Frame Data   */
            for (int i = 0; i < mRigidBodies.Count; i++)
            {
                int rbID = mRigidBodies[i].ID;              // Fetching rigid body IDs from the saved descriptions

                for (int j = 0; j < data.nRigidBodies; j++)
                {
                    if(rbID == data.RigidBodies[j].ID)      // When rigid body ID of the descriptions matches rigid body ID of the frame data.
                    {
                        NatNetML.RigidBody rb = mRigidBodies[i];                // Saved rigid body descriptions
                        NatNetML.RigidBodyData rbData = data.RigidBodies[j];    // Received rigid body descriptions

                        if (rbData.Tracked == true)
                        {
#if DEBUG_MSG
                            Console.WriteLine("\tRigidBody ({0}):", rb.Name);
                            Console.WriteLine("\t\tpos ({0:N3}, {1:N3}, {2:N3})", rbData.x, rbData.y, rbData.z);

                            // Rigid Body Euler Orientation
                            float[] quat = new float[4] { rbData.qx, rbData.qy, rbData.qz, rbData.qw };
                            float[] eulers = new float[3];

                            eulers = NatNetClientML.QuatToEuler(quat, NATEulerOrder.NAT_XYZr); //Converting quat orientation into XYZ Euler representation.
                            double xrot = RadiansToDegrees(eulers[0]);
                            double yrot = RadiansToDegrees(eulers[1]);
                            double zrot = RadiansToDegrees(eulers[2]);
                            Console.WriteLine("\t\tori ({0:N3}, {1:N3}, {2:N3})", xrot, yrot, zrot);
#endif
                            if (drones.ContainsKey(rb.Name))
                            {
                                DroneData drone = drones[rb.Name];
                                drone.lost_count = 0;
                                long cur_ms = stopwatch.ElapsedMilliseconds;
                                if (drone.send_count > 0)
                                {
                                    drone.send_count--;
                                }
                                else if (data_gogo)
                                {
                                    drone.send_count = 10;
                                    data_gogo = false;
                                    MAVLink.mavlink_att_pos_mocap_t att_pos = new MAVLink.mavlink_att_pos_mocap_t();
                                    att_pos.time_usec = (ulong)(cur_ms * 1000);
                                    att_pos.x = rbData.x; //north
                                    att_pos.y = rbData.z; //east
                                    att_pos.z = -rbData.y; //down
                                    att_pos.q = new float[4] { rbData.qw, rbData.qx, rbData.qz, -rbData.qy };
                                    byte[] pkt = mavlinkParse.GenerateMAVLinkPacket10(MAVLink.MAVLINK_MSG_ID.ATT_POS_MOCAP, att_pos);

                                    if (drone.lastTime >= 0)
                                    {
                                        MAVLink.mavlink_vision_speed_estimate_t vis_speed = new MAVLink.mavlink_vision_speed_estimate_t();
                                        float total_s = (cur_ms - drone.lastTime) * 0.001f;
                                        vis_speed.x = (rbData.x - drone.lastPosN) / total_s;
                                        vis_speed.y = (rbData.z - drone.lastPosE) / total_s;
                                        vis_speed.z = (-rbData.y - drone.lastPosD) / total_s;
                                        vis_speed.usec = (ulong)(cur_ms * 1000);
                                        byte[] pkt2 = mavlinkParse.GenerateMAVLinkPacket10(MAVLink.MAVLINK_MSG_ID.VISION_SPEED_ESTIMATE, vis_speed);
                                        int total_len = pkt.Length + pkt2.Length;
                                        byte[] uwb_data = new byte[10 + total_len + 1];
                                        uwb_data[0] = 0x54;
                                        uwb_data[1] = 0xf1;
                                        uwb_data[2] = 0xff;
                                        uwb_data[3] = 0xff;
                                        uwb_data[4] = 0xff;
                                        uwb_data[5] = 0xff;
                                        uwb_data[6] = 2;
                                        uwb_data[7] = drone.uwb_tag_id;
                                        uwb_data[8] = (byte)(total_len & 0xff);
                                        uwb_data[9] = (byte)(total_len >> 16);
                                        Array.Copy(pkt, 0, uwb_data, 10, pkt.Length);
                                        Array.Copy(pkt2, 0, uwb_data, 10 + pkt.Length, pkt2.Length);
                                        byte chk_sum = 0;
                                        foreach (byte uwb_data_byte in uwb_data)
                                        {
                                            chk_sum += uwb_data_byte;
                                        }
                                        uwb_data[uwb_data.Length - 1] = chk_sum;
                                        mavSock.SendTo(uwb_data, drone.ep);
                                    }
                                }
                                drone.lastTime = cur_ms;
                                drone.lastPosN = rbData.x;
                                drone.lastPosE = rbData.z;
                                drone.lastPosD = -rbData.y;
                            }
                        }
                        else
                        {
                            Console.WriteLine("\t{0} is not tracked in current frame", rb.Name);
                            /*if (drones.ContainsKey(rb.Name))
                            {
                                DroneData drone = drones[rb.Name];
                                drone.lost_count++;
                                if (drone.lost_count > 3)
                                {
                                    drone.lost_count = 0;
                                    MAVLink.mavlink_gps_input_t gps_input = new MAVLink.mavlink_gps_input_t();
                                    gps_input.gps_id = 0;
                                    gps_input.fix_type = (byte)MAVLink.GPS_FIX_TYPE.NO_FIX;
                                    byte[] pkt = mavlinkParse.GenerateMAVLinkPacket10(MAVLink.MAVLINK_MSG_ID.GPS_INPUT, gps_input);
                                    mavSock.SendTo(pkt, drone.ep);
                                }
                            }*/
                        }
                    }
                }
            }

            /* Parsing Skeleton Frame Data  */
            for (int i = 0; i < mSkeletons.Count; i++)      // Fetching skeleton IDs from the saved descriptions
            {
                int sklID = mSkeletons[i].ID;

                for (int j = 0; j < data.nSkeletons; j++)
                {
                    if (sklID == data.Skeletons[j].ID)      // When skeleton ID of the description matches skeleton ID of the frame data.
                    {
                        NatNetML.Skeleton skl = mSkeletons[i];              // Saved skeleton descriptions
                        NatNetML.SkeletonData sklData = data.Skeletons[j];  // Received skeleton frame data

                        Console.WriteLine("\tSkeleton ({0}):", skl.Name);
                        Console.WriteLine("\t\tSegment count: {0}", sklData.nRigidBodies);

                        /*  Now, for each of the skeleton segments  */
                        for (int k = 0; k < sklData.nRigidBodies; k++)
                        {
                            NatNetML.RigidBodyData boneData = sklData.RigidBodies[k];

                            /*  Decoding skeleton bone ID   */
                            int skeletonID = HighWord(boneData.ID);
                            int rigidBodyID = LowWord(boneData.ID);
                            int uniqueID = skeletonID * 1000 + rigidBodyID;
                            int key = uniqueID.GetHashCode();

                            NatNetML.RigidBody bone = (RigidBody)mHtSkelRBs[key];   //Fetching saved skeleton bone descriptions

                            //Outputting only the hip segment data for the purpose of this sample.
                            if (k == 0)
                                Console.WriteLine("\t\t{0:N3}: pos({1:N3}, {2:N3}, {3:N3})", bone.Name, boneData.x, boneData.y, boneData.z);
                        }
                    }
                }
            }
            
            /*  Parsing Force Plate Frame Data  */
            for (int i = 0; i < mForcePlates.Count; i++)
            {
                int fpID = mForcePlates[i].ID;                  // Fetching force plate IDs from the saved descriptions

                for (int j = 0; j < data.nForcePlates; j++)
                {
                    if (fpID == data.ForcePlates[j].ID)         // When force plate ID of the descriptions matches force plate ID of the frame data.
                    {
                        NatNetML.ForcePlate fp = mForcePlates[i];                // Saved force plate descriptions
                        NatNetML.ForcePlateData fpData = data.ForcePlates[i];    // Received forceplate frame data

                        Console.WriteLine("\tForce Plate ({0}):", fp.Serial);

                        // Here we will be printing out only the first force plate "subsample" (index 0) that was collected with the mocap frame.
                        for (int k = 0; k < fpData.nChannels; k++)
                        {
                            Console.WriteLine("\t\tChannel {0}: {1}", fp.ChannelNames[k], fpData.ChannelData[k].Values[0]);
                        }
                    }
                }
            }
#if DEBUG_MSG
            Console.WriteLine("\n");
#endif
        }

        static void connectToServer()
        {
            /*  [NatNet] Instantiate the client object  */
            mNatNet = new NatNetML.NatNetClientML();

            /*  [NatNet] Checking verions of the NatNet SDK library  */
            int[] verNatNet = new int[4];           // Saving NatNet SDK version number
            verNatNet = mNatNet.NatNetVersion();
            Console.WriteLine("NatNet SDK Version: {0}.{1}.{2}.{3}", verNatNet[0], verNatNet[1], verNatNet[2], verNatNet[3]);

            /*  [NatNet] Connecting to the Server    */
            Console.WriteLine("\nConnecting...\n\tLocal IP address: {0}\n\tServer IP Address: {1}\n\n", mStrLocalIP, mStrServerIP);

            NatNetClientML.ConnectParams connectParams = new NatNetClientML.ConnectParams();
            connectParams.ConnectionType = mConnectionType;
            connectParams.ServerAddress = mStrServerIP;
            connectParams.LocalAddress = mStrLocalIP;
            mNatNet.Connect( connectParams );
        }

        static bool fetchServerDescriptor()
        {
            NatNetML.ServerDescription m_ServerDescriptor = new NatNetML.ServerDescription();
            int errorCode = mNatNet.GetServerDescription(m_ServerDescriptor);

            if (errorCode == 0)
            {
                Console.WriteLine("Success: Connected to the server\n");
                parseSeverDescriptor(m_ServerDescriptor);
                return true;
            }
            else
            {
                Console.WriteLine("Error: Failed to connect. Check the connection settings.");
                Console.WriteLine("Program terminated (Enter ESC to exit)");
                return false;
            }
        }

        static void parseSeverDescriptor(NatNetML.ServerDescription server)
        {
            Console.WriteLine("Server Info:");
            Console.WriteLine("\tHost: {0}", server.HostComputerName);
            Console.WriteLine("\tApplication Name: {0}", server.HostApp);
            Console.WriteLine("\tApplication Version: {0}.{1}.{2}.{3}", server.HostAppVersion[0], server.HostAppVersion[1], server.HostAppVersion[2], server.HostAppVersion[3]);
            Console.WriteLine("\tNatNet Version: {0}.{1}.{2}.{3}\n", server.NatNetVersion[0], server.NatNetVersion[1], server.NatNetVersion[2], server.NatNetVersion[3]);
        }

        static void fetchDataDescriptor()
        {
            /*  [NatNet] Fetch Data Descriptions. Instantiate objects for saving data descriptions and frame data    */        
            bool result = mNatNet.GetDataDescriptions(out mDataDescriptor);
            if (result)
            {
                Console.WriteLine("Success: Data Descriptions obtained from the server.");
                parseDataDescriptor(mDataDescriptor);
            }
            else
            {
                Console.WriteLine("Error: Could not get the Data Descriptions");
            }
            Console.WriteLine("\n");
        }

        static void parseDataDescriptor(List<NatNetML.DataDescriptor> description)
        {
            //  [NatNet] Request a description of the Active Model List from the server. 
            //  This sample will list only names of the data sets, but you can access 
            int numDataSet = description.Count;
            Console.WriteLine("Total {0} data sets in the capture:", numDataSet);

            for (int i = 0; i < numDataSet; ++i)
            {
                int dataSetType = description[i].type;
                // Parse Data Descriptions for each data sets and save them in the delcared lists and hashtables for later uses.
                switch (dataSetType)
                {
                    case ((int) NatNetML.DataDescriptorType.eMarkerSetData):
                        NatNetML.MarkerSet mkset = (NatNetML.MarkerSet)description[i];
                        Console.WriteLine("\tMarkerSet ({0})", mkset.Name);
                        break;


                    case ((int) NatNetML.DataDescriptorType.eRigidbodyData):
                        NatNetML.RigidBody rb = (NatNetML.RigidBody)description[i];
                        Console.WriteLine("\tRigidBody ({0})", rb.Name);

                        // Saving Rigid Body Descriptions
                        mRigidBodies.Add(rb);
                        break;


                    case ((int) NatNetML.DataDescriptorType.eSkeletonData):
                        NatNetML.Skeleton skl = (NatNetML.Skeleton)description[i];
                        Console.WriteLine("\tSkeleton ({0}), Bones:", skl.Name);

                        //Saving Skeleton Descriptions
                        mSkeletons.Add(skl);

                        // Saving Individual Bone Descriptions
                        for (int j = 0; j < skl.nRigidBodies; j++)
                        {

                            Console.WriteLine("\t\t{0}. {1}", j + 1, skl.RigidBodies[j].Name);
                            int uniqueID = skl.ID * 1000 + skl.RigidBodies[j].ID;
                            int key = uniqueID.GetHashCode();
                            mHtSkelRBs.Add(key, skl.RigidBodies[j]); //Saving the bone segments onto the hashtable
                        }
                        break;


                    case ((int) NatNetML.DataDescriptorType.eForcePlateData):
                        NatNetML.ForcePlate fp = (NatNetML.ForcePlate)description[i];
                        Console.WriteLine("\tForcePlate ({0})", fp.Serial);

                        // Saving Force Plate Channel Names
                        mForcePlates.Add(fp);
               
                        for (int j = 0; j < fp.ChannelCount; j++)
                        {
                            Console.WriteLine("\t\tChannel {0}: {1}", j + 1, fp.ChannelNames[j]);
                        }
                        break;

                    default:
                        // When a Data Set does not match any of the descriptions provided by the SDK.
                        Console.WriteLine("\tError: Invalid Data Set");
                        break;
                }
            }
        }

        static double RadiansToDegrees(double dRads)
        {
            return dRads * (180.0f / Math.PI);
        }
        
        static int LowWord(int number)
        {
            return number & 0xFFFF;
        }

        static int HighWord(int number)
        {
            return ((number >> 16) & 0xFFFF);
        }
    } // End. ManagedClient class
} // End. NatNetML Namespace
