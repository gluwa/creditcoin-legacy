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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;

namespace ccplugin
{
    public class Loader<Plugin>
    {
        private Dictionary<string, Plugin> plugins = new Dictionary<string, Plugin>();
        private const string dlls = "*.dll";

        public void Load(string folder, List<string> msgs)
        {
            string[] dllFileNames = Directory.GetFiles(folder, dlls);
            ICollection<Assembly> assemblies = new List<Assembly>(dllFileNames.Length);
            foreach (string dllFile in dllFileNames)
            {
                try
                {
                    Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(dllFile);
                    assemblies.Add(assembly);
                }
                catch (Exception x)
                {
                    msgs.Add($"Failed to load {dllFile}: {x.Message}");
                }
            }

            Type pluginType = typeof(Plugin);
            ICollection<Type> pluginTypes = new List<Type>();
            foreach (Assembly assembly in assemblies)
            {
                if (assembly != null)
                {
                    Type[] types = null;

                    try
                    {
                        types = assembly.GetTypes();
                    }
                    catch(ReflectionTypeLoadException e)
                    { 
                        types = e.Types;
                    }

                    foreach (Type type in types)
                    {
                        if (type == null || type.IsInterface || type.IsAbstract)
                        {
                            continue;
                        }
                        else
                        {
                            if (type.GetInterface(pluginType.FullName) != null)
                            {
                                pluginTypes.Add(type);
                            }
                        }
                    }
                }
            }

            foreach (Type type in pluginTypes)
            {
                Plugin plugin = (Plugin)Activator.CreateInstance(type);
                plugins.Add(type.Name.ToLower(), plugin);
            }
        }

        public Plugin Get(string name)
        {
            Plugin plugin;
            plugins.TryGetValue(name, out plugin);
            return plugin;
        }
    }

    public interface ITxBuilder
    {
        byte[] BuildTx(string[] command, out string msg);
    }

    public class RpcHelper
    {
        private const string DATA = "data";
        private const string LINK = "link";
        private const string STATUS = "status";
        private const string ERROR = "error";
        private const string MESSAGE = "message";

        public static string CompleteBatch(HttpClient httpClient, string url, ByteArrayContent content)
        {
            var responseMessage = httpClient.PostAsync(url, content).Result;
            var json = responseMessage.Content.ReadAsStringAsync().Result;
            var response = JObject.Parse(json);
            Debug.Assert(response.ContainsKey(LINK));
            var link = (string)response[LINK];
            for (; ; )
            {
                responseMessage = httpClient.GetAsync(link).Result;
                json = responseMessage.Content.ReadAsStringAsync().Result;
                response = JObject.Parse(json);
                Debug.Assert(response.ContainsKey(DATA));
                var data = (JArray)response[DATA];
                Debug.Assert(data.Count == 1);
                var obj = (JObject)data[0];
                Debug.Assert(obj.ContainsKey(STATUS));
                var status = (string)obj[STATUS];
                if (status.Equals("INVALID"))
                    return "Error: request rejected";
                else if (status.Equals("COMMITTED"))
                    break;
                else
                    System.Threading.Thread.Sleep(500);
            }
            return "Success";
        }

        public static byte[] ReadProtobuf(HttpClient httpClient, string url, out string msg)
        {
            var response = httpClient.GetAsync(url).Result;
            var dealStateJson = response.Content.ReadAsStringAsync().Result;
            var dealStateObj = JObject.Parse(dealStateJson);
            if (dealStateObj.ContainsKey(ERROR))
            {
                msg = (string)dealStateObj[ERROR][MESSAGE];
                return null;
            }

            Debug.Assert(dealStateObj.ContainsKey(DATA));
            string data = (string)dealStateObj[DATA];
            byte[] protobuf = Convert.FromBase64String(data);
            msg = null;
            return protobuf;
        }

        public static byte[] Hex2Bytes(string input)
        {
            Debug.Assert(input.Length % 2 == 0);
            var outputLength = input.Length / 2;
            var output = new byte[outputLength];
            for (var i = 0; i < outputLength; i++)
                output[i] = Convert.ToByte(input.Substring(i * 2, 2), 16);
            return output;
        }
    }

    public interface ICCClientPlugin
    {
        bool Run(IConfiguration cfg, HttpClient httpClient, ITxBuilder txBuilder, Dictionary<string, string> settings, string pluginsFolder, string url, string[] command, out bool inProgress, out string msg);
    }

    public interface ICCGatewayPlugin
    {
        bool Run(IConfiguration cfg, string[] command, out string msg);
    }
}
