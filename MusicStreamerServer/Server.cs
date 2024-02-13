using ATL;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MusicStreamerServer
{
    internal class Server
    {
        private static readonly List<Socket> _clients = [];
        private static readonly object _clientLock = new();

        internal static bool SongFinished = false;

        private enum DATA_CODE : byte //Identifier codes for packets sent to clients
        {
            INVALID = 0,
            SONG = 1, //packet with song-bytes
            SONGLIST = 2, //partial packet for song library transfer
            SONGLIST_END = 3, //song library fully transferred signal
            NEXTQUEUE = 4, //packet with index of next queued song
            NEXTPIC = 5, //partial packet for cover image transfer of next queued song
            NEXTPIC_END = 6, //cover image of next queued song fully transferred signal
            SONG_END = 7, //current song fully transferred signal
        }

        /// <summary>
        /// Opens Server listening socket, then continuously accepts new clients, sends them the Player.SongList and listens to responses
        /// </summary>
        /// <param name="state">Unused</param>
        internal static void AcceptConnections(object? state)
        {
            IPEndPoint endPoint = new(IPAddress.Parse("127.0.0.1"), 35555); //hardcoded local-IP and port 35555 as server listening port
            Socket server = new(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            server.Bind(endPoint);
            server.Listen();

            do
            {
                Socket handler = server.Accept();
                try
                {
                    SendSongList(handler);
                    ThreadPool.QueueUserWorkItem(ReceiveQueues, handler);

                    lock(_clientLock)
                    {
                        _clients.Add(handler);
                        Console.WriteLine("Client connected: " + handler.LocalEndPoint.Serialize().ToString());
                    }
                }
                catch(SocketException e)
                {
                    Console.WriteLine("Client-Socket error during connection process.");
                    Console.WriteLine(e.ToString());
                }
                
            } while(true);
        }

        /// <summary>
        /// Sends comma seperated UTF8 string-representation of Player.SongList to client
        /// </summary>
        /// <param name="client">Socket of the client to send the SongList to</param>
        private static void SendSongList(Socket client)
        {
            //Prepare comma seperated string-representation of list
            string songsString = "";
            foreach(string song in Player.SongList)
            {
                songsString += song + ";";
            }
            songsString = songsString.Remove(songsString.Length - 1);

            //Transfer list in as many packets as needed
            byte[] songsArray = Encoding.UTF8.GetBytes(songsString);
            List<byte[]> packets = SplitPackets(songsArray, (byte)DATA_CODE.SONGLIST);
            foreach(byte[] packet in packets)
            {
                client.Send(packet, SocketFlags.None);
            }

            //Send signal that list has been fully transferred
            byte[] endSignal = new byte[2049];
            endSignal[0] = (byte)DATA_CODE.SONGLIST_END;
            client.Send(endSignal, SocketFlags.None);
        }

        /// <summary>
        /// Receives data from client socket and calls Player.QueueSong with received data
        /// </summary>
        /// <param name="state">Socket of the client to listen to</param>
        private static void ReceiveQueues(object state)
        {
            Socket client = (Socket)state;
            try
            {
                do
                {
                    byte[] bytes = new byte[4];
                    var received = client.Receive(bytes, SocketFlags.None);
                    int index = BitConverter.ToInt32(bytes.AsSpan()[0..received]);
                    Player.QueueSong(index);
                } while(true);
            }
            catch(SocketException) //Remove client if SocketException occurs
            {
                lock(_clientLock)
                {
                    _clients.Remove(client);
                }
            }
        }

        /// <summary>
        /// Multicasts next queued index and cover image
        /// </summary>
        /// <param name="songIndex">Index of next queued song</param>
        /// <param name="track">ATL.Track object of next queued song used for cover image transfer</param>
        internal static void SendNextQueue(int songIndex, Track track)
        {
            //Send index and total duration of next queued song
            byte[] data = new byte[2049];
            data[0] = (byte)DATA_CODE.NEXTQUEUE;

            byte[] indexBytes = BitConverter.GetBytes(songIndex);
            for(int i = 0; i < indexBytes.Length; i++)
            {
                data[i+1] = indexBytes[i];
            }

            MultiCast(data);

            //Send pic if exists, if no pic exists only sends NEXTPIC_END code so client knows there is no pic for next queued song
            if(track.EmbeddedPictures.Count > 0)
            {
                List<byte[]> packets = SplitPackets(track.EmbeddedPictures[0].PictureData, (byte)DATA_CODE.NEXTPIC);
                foreach(byte[] packet in packets)
                {
                    MultiCast(packet);
                }

            }

            data = new byte[2049];
            data[0] = (byte)DATA_CODE.NEXTPIC_END;
            MultiCast(data);
        }
        

        /// <summary>
        /// Sends Player.CurrBytes to all _clients until SongFinished
        /// </summary>
        internal static void StreamSong()
        {
            //Send song data in a loop everytime Player.CurrBytes has changed
            while(!SongFinished)
            {
                if(Player.CurrBytesChanged)
                {
                    lock(Player.CurrBytesLock)
                    {
                        byte[] data = new byte[2049];
                        data[0] = (byte)DATA_CODE.SONG;
                        for(int i = 0; i < Player.CurrBytes.Length; i++)
                        {
                            data[i + 1] = Player.CurrBytes[i];
                        }

                        MultiCast(data);

                        Player.CurrBytesChanged = false;
                    }
                }
            }
            
            //After song is finished send Player.CurrBytes one more time if needed
            if(Player.CurrBytesChanged)
            {
                lock(Player.CurrBytesLock)
                {
                    byte[] data = new byte[2049];
                    data[0] = (byte)DATA_CODE.SONG;
                    for(int i = 0; i < Player.CurrBytes.Length; i++)
                    {
                        data[i + 1] = Player.CurrBytes[i];
                    }

                    MultiCast(data);

                    Player.CurrBytesChanged = false;
                }
            }

            //Multicast end of song
            byte[] enddata = new byte[2049];
            enddata[0] = (byte)DATA_CODE.SONG_END;
            MultiCast(enddata);

            SongFinished = false;
        }

        #region Utility
        /// <summary>
        /// Splits <paramref name="data"/> into as many packets as needed for a transfer
        /// </summary>
        /// <param name="data">byte[] data to be split into multiple packets</param>
        /// <param name="data_code">DATA_CODE to use in the packets</param>
        /// <returns>Returns a list of packets containing <paramref name="data"/> and using <paramref name="data_code"/></returns>
        private static List<byte[]> SplitPackets(byte[] data, byte data_code)
        {
            List<byte[]> packets = [];

            for(int i = 0; i < data.Length; i+=2048)
            {
                byte[] packet = new byte[2049];
                packet[0] = data_code;

                byte[] bytes;
                int endIndex = i + 2048;
                if(endIndex > data.Length)
                {
                    bytes = data[i..^0];
                }
                else
                {
                    bytes = data[i..endIndex];
                }

                for(int j = 0; j < bytes.Length; j++)
                {
                    packet[j + 1] = bytes[j];
                }
                packets.Add(packet);
            }

            return packets;
        }

        /// <summary>
        /// Sends <paramref name="data"/> to all clients in _clients
        /// </summary>
        /// <param name="data">Data to be sent to all clients</param>
        private static void MultiCast(byte[] data)
        {
            List<Socket> closedConnections = [];
            lock(_clientLock)
            {
                foreach(Socket client in _clients)
                {
                    try
                    {

                        client.Send(data, SocketFlags.None);
                    }
                    catch(SocketException)
                    {
                        closedConnections.Add(client);
                    }
                }

                //Remove clients where SocketException occured during transfer
                foreach(Socket client in closedConnections)
                {
                    _clients.Remove(client);
                }
            }
        }
        #endregion Utility
    }
}
