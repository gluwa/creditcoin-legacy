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
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Configuration;

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
                    catch (ReflectionTypeLoadException e)
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
        string getSighash();
    }

    public interface ICCClientPlugin
    {
        bool Run(bool txid, IConfiguration cfg, string secretOverride, HttpClient httpClient, ITxBuilder txBuilder, ref string progressToken, string url, string[] command, out bool inProgress, out string msg, out string link);
    }

    public interface ICCGatewayPlugin
    {
        bool Run(IConfiguration cfg, string[] command, out string msg);
    }
}