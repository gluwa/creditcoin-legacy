/*
	Copyright(c) 2018 Gluwa, Inc.

	This file is part of Creditcoin.

	Creditcoin is free software: you can redistribute it and/or modify
	it under the terms of the GNU Lesser General Public License as published by
	the Free Software Foundation, either version 3 of the License, or
	(at your option) any later version.
	
	This program is distributed in the hope that it will be useful,
	but WITHOUT ANY WARRANTY; without even the implied warranty of
	MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
	GNU Lesser General Public License for more details.
	
	You should have received a copy of the GNU Lesser General Public License
	along with Creditcoin. If not, see <https://www.gnu.org/licenses/>.
*/

using ccplugin;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NetMQ;
using NetMQ.Sockets;
using Microsoft.Extensions.Configuration;

namespace ccgateway
{
    class Program
    {
        static void Main(string[] args)
        {
            IConfiguration config = new ConfigurationBuilder()
                    .AddJsonFile("appsettings.json", true, false)
                    .AddJsonFile("appsettings.dev.json", true, true)
                .Build();

            string signerHexStr = config["signer"];
            if (string.IsNullOrWhiteSpace(signerHexStr))
            {
                Console.WriteLine("Signer is not configured");
                return;
            }

            string ip = config["bindIP"];
            if (string.IsNullOrWhiteSpace(ip))
            {
                Console.WriteLine("bindIP is not set.. defaulting to 127.0.0.1 local connection only");
                ip = "127.0.0.1";
            }

            using (var socket = new ResponseSocket())
            {
                socket.Bind($"tcp://{ip}:55555");

                while (true)
                {
                    Console.WriteLine("running");
                    string requestString = socket.ReceiveFrameString();

                    string[] command = requestString.Split();
                    string response;

                    if (command.Length < 2)
                    {
                        response = "poor";
                        Console.WriteLine(string.Empty);
                    }
                    else
                    {
                        var loader = new Loader<ICCGatewayPlugin>();
                        var msgs = new List<string>();

                        string root = Directory.GetCurrentDirectory();
                        string folder = TxBuilder.GetPluginsFolder(root);
                        if (folder == null)
                        {
                            response = "fail";
                        }
                        else
                        {
                            loader.Load(folder, msgs);
                            foreach (var msg in msgs)
                            {
                                Console.WriteLine(msg);
                            }

                            string action = command[0];
                            command = command.Skip(1).ToArray();

                            ICCGatewayPlugin plugin = loader.Get(action);
                            var pluginConfig = config.GetSection(action);
                            if (plugin == null)
                            {
                                response = "miss";
                            }
                            else
                            {
                                string msg;
                                bool done = plugin.Run(pluginConfig, signerHexStr, command, out msg);
                                if (done)
                                {
                                    if (msg == null)
                                    {
                                        msg = "Success!";
                                    }
                                    response = "good";
                                }
                                else
                                {
                                    response = "fail";
                                }
                            }
                        }
                    }

                    Console.WriteLine(response);
                    socket.SendFrame(response);
                }
            }
        }
    }
}