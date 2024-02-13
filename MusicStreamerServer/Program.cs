// See https://aka.ms/new-console-template for more information
using MusicStreamerServer;


Player.LoadSongs();

ThreadPool.QueueUserWorkItem(Server.AcceptConnections);

Player.StartPlaying();