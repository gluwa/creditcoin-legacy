using ccplugin;
using Microsoft.Extensions.Configuration;
using Sawtooth.Sdk.Client;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;

namespace ccclient
{
    class Program
    {
        private const string configParamPrefix = "-config:";
        private const string progressParamPrefix = "-progress:";
        private const string pluginsParamPrefix = "-plugins:";
        private const string txidParam = "-txid";

        static void Main(string[] args)
        {
            string root = Directory.GetCurrentDirectory();
            string pluginFolder;

            if (args.Length > 0 && args[0].StartsWith(pluginsParamPrefix))
            {
                pluginFolder = args[0].Substring(pluginsParamPrefix.Length);
                args = args.Skip(1).ToArray();
            }
            else
            {
                pluginFolder = TxBuilder.GetPluginsFolder(root);
            }

            if (pluginFolder == null)
            {
                Console.WriteLine("plugins subfolder not found");
                args = new string[0];
            }

            string progressId = "";
            bool ignoreOldProgress = false;
            if (args.Length > 0 && args[0].StartsWith(progressParamPrefix))
            {
                progressId = args[0].Substring(progressParamPrefix.Length);
                if (progressId[0] == '*')
                {
                    ignoreOldProgress = true;
                    progressId = progressId.Substring(1);
                }
                args = args.Skip(1).ToArray();
            }

            string progress = null;
            if (args.Length > 0)
            {
                progress = Path.Combine(pluginFolder, $"progress{progressId}.txt");
                if (ignoreOldProgress)
                {
                    File.Delete(progress);
                }

                if (File.Exists(progress))
                {
                    var interruptedCommand = File.ReadAllText(progress);
                    Console.WriteLine($"Found unfinished action, if a command is given it will be ignored, instead retrying:\n{interruptedCommand}");
                    args = interruptedCommand.Split();
                }
                else
                {
                    File.WriteAllText(progress, string.Join(' ', args));
                }
            }

            if (args.Length < 1)
            {
                Console.WriteLine("Usage: ccclient [-plugins:pluginsFolderPath] [-progress:[*]progressId] [-config:configFileName] [-txid] command [parameters]");
                Console.WriteLine("commands:");
                Console.WriteLine("sighash");
                Console.WriteLine("tip [numBlocksBelow]");
                Console.WriteLine("list Settings");
                Console.WriteLine("list Wallets");
                Console.WriteLine("list Addresses");
                Console.WriteLine("list Transfers");
                Console.WriteLine("list AskOrders");
                Console.WriteLine("list BidOrders");
                Console.WriteLine("list Offers");
                Console.WriteLine("list DealOrders");
                Console.WriteLine("list RepaymentOrders");
                Console.WriteLine("show Balance sighash|0");
                Console.WriteLine("show Address sighash|0 blockchain address network");
                Console.WriteLine("show MatchingOrders sighash|0");
                Console.WriteLine("show CurrentOffers sighash|0");
                Console.WriteLine("show CreditHistory sighash|0");
                Console.WriteLine("show NewDeals sighash|0");
                Console.WriteLine("show Transfer sighash|0 orderId");
                Console.WriteLine("show CurrentLoans sighash|0");
                Console.WriteLine("show LockedLoans sighash|0");
                Console.WriteLine("show NewRepaymentOrders sighash|0");
                Console.WriteLine("show CurrentRepaymentOrders sighash|0");
                Console.WriteLine("creditcoin SendFunds amount sighash");
                Console.WriteLine("creditcoin RegisterAddress blockchain address network");
                Console.WriteLine("creditcoin RegisterTransfer gain orderId txId");
                Console.WriteLine("creditcoin AddAskOrder addressId amount interest maturity fee expiration");
                Console.WriteLine("creditcoin AddBidOrder addressId amount interest maturity fee expiration");
                Console.WriteLine("creditcoin AddOffer askOrderId bidOrderId expiration");
                Console.WriteLine("creditcoin AddDealOrder offerId expiration");
                Console.WriteLine("creditcoin CompleteDealOrder dealOrderId transferId");
                Console.WriteLine("creditcoin LockDealOrder dealOrderId");
                Console.WriteLine("creditcoin CloseDealOrder dealOrderId transferId");
                Console.WriteLine("creditcoin Exempt dealOrderId transferId");
                Console.WriteLine("creditcoin AddRepaymentOrder dealOrderId addressId amount expiration");
                Console.WriteLine("creditcoin CompleteRepaymentOrder repaymentOrderId");
                Console.WriteLine("creditcoin CloseRepaymentOrder repaymentOrderId transferId");
                Console.WriteLine("creditcoin CollectCoins addressId amount txId");
                Console.WriteLine("bitcoin RegisterTransfer gain orderId sourceTxId");
                Console.WriteLine("ethereum RegisterTransfer gain orderId");
                Console.WriteLine("ethereum CollectCoins amount");
                return;
            }

            string configFile = null;
            if (args.Length > 0 && args[0].StartsWith(configParamPrefix))
            {
                configFile = args[0].Substring(configParamPrefix.Length);
                args = args.Skip(1).ToArray();
                if (!File.Exists(configFile))
                {
                    configFile = Path.Combine(pluginFolder, configFile);
                    if (!File.Exists(configFile))
                    {
                        Console.WriteLine("Cannot find the specified config file");
                        return;
                    }
                }
            }
            bool txid = false;
            if (args.Length > 0 && args[0].Equals(txidParam))
            {
                args = args.Skip(1).ToArray();
                txid = true;
            }

            if (args.Length < 1)
            {
                Console.WriteLine("Command is not provided");
                return;
            }

            var builder = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", true, false);
#if DEBUG
            builder = builder
                .AddJsonFile("appsettings.dev.json", true, false);
#endif
            ;

            if (configFile != null)
            {
                builder.AddJsonFile(configFile, true, false);
            }

            IConfiguration config = builder.Build();

            string creditcoinRestApiURL = config["creditcoinRestApiURL"];
            string creditcoinUrl = string.IsNullOrWhiteSpace(creditcoinRestApiURL)? "http://localhost:8008": creditcoinRestApiURL;
            HttpClient httpClient = new HttpClient();

            string progressToken = null;
            string pluginProgress = Path.Combine(pluginFolder, $"plugin_progress{progressId}.txt");
            if (File.Exists(pluginProgress))
            {
                Console.WriteLine("Found unfinished action, retrying...");
                progressToken = File.ReadAllText(pluginProgress);
            }

            Signer signer = cccore.Core.getSigner(config, null);

            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args, config, txid, pluginFolder, progressToken, signer, out bool inProgress, null, out string link);
            Debug.Assert(output == null && link != null || link == null);
            if (output == null)
            {
                for (; ; )
                {
                    System.Threading.Thread.Sleep(500);
                    var msg = cccore.Core.Run(httpClient, creditcoinUrl, link, txid);
                    if (msg != null)
                    {
                        Console.WriteLine(msg);
                        break;
                    }
                }
            }
            else
            {
                foreach (var line in output)
                {
                    Console.WriteLine(line);
                }
            }

            if (!inProgress)
            {
                File.Delete(progress);
                File.Delete(pluginProgress);
            }
        }
    }
}
