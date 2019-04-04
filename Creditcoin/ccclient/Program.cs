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
using Microsoft.Extensions.Configuration;
using Sawtooth.Sdk.Client;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using System.Numerics;

namespace ccclient
{
    class Program
    {
        private static string creditcoinUrl = "http://localhost:8008";

        private static HttpClient httpClient = new HttpClient();

        private const string ERROR = "error";
        private const string DATA = "data";
        private const string ADDRESS = "address";
        private const string MESSAGE = "message";
        private const string PAGING = "paging";
        private const string NEXT = "next";

        private const string configParamPrefix = "-config:";
        private const string progressParamPrefix = "-progress:";
        private const string txidParam = "-txid";

        static void Main(string[] args)
        {
            try
            {
                string root = Directory.GetCurrentDirectory();
                string pluginFolder = TxBuilder.GetPluginsFolder(root);
                if (pluginFolder == null)
                {
                    Console.WriteLine("plugins subfolder not found");
                    return;
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

                string progress = Path.Combine(pluginFolder, $"progress{progressId}.txt");
                if (ignoreOldProgress)
                {
                    File.Delete(progress);
                }

                if (File.Exists(progress))
                {
                    Console.WriteLine("Found unfinished action, retrying...");
                    args = File.ReadAllText(progress).Split();
                }
                else if (args.Length > 0)
                {
                    File.WriteAllText(progress, string.Join(' ', args));
                }

                if (args.Length < 1)
                {
                    Console.WriteLine("Usage: ccclient [-progress:[*]progressId] [-config:configFileName] [-txid] command [parameters]");
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
                    Console.WriteLine("show NewRepaymentOrders sighash|0");
                    Console.WriteLine("show CurrentRepaymentOrders sighash|0");
                    Console.WriteLine("creditcoin SendFunds amount sighash ");
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

                string action;
                string[] command;

                var builder = new ConfigurationBuilder()
                    .AddJsonFile("appsettings.json", true, false)
#if DEBUG
                    .AddJsonFile("appsettings.dev.json", true, false)
#endif
                    ;

                if (configFile != null)
                {
                    builder.AddJsonFile(configFile, true, false);
                }

                IConfiguration config = builder.Build();

                string creditcoinRestApiURL = config["creditcoinRestApiURL"];
                if (!string.IsNullOrWhiteSpace(creditcoinRestApiURL))
                {
                    creditcoinUrl = creditcoinRestApiURL;
                }

                Signer signer = getSigner(config);

                if (args.Length < 1)
                {
                    Console.WriteLine("Command is not provided");
                    return;
                }

                action = args[0].ToLower();
                command = args.Skip(1).ToArray();

                bool inProgress = false;

                if (action.Equals("sighash"))
                {
                    Console.WriteLine(TxBuilder.getSighash(signer));
                }
                else if (action.Equals("tip"))
                {
                    BigInteger headIdx = GetHeadIdx();
                    if (command.Length == 1)
                    {
                        BigInteger num;
                        if (!BigInteger.TryParse(command[0], out num))
                        {
                            throw new Exception("Invalid numerics");
                        }
                        headIdx -= num;
                    }
                    Console.WriteLine(headIdx);
                }
                else if (action.Equals("list"))
                {
                    if (command.Length > 2)
                    {
                        throw new Exception("1 or 2 parametersd expected");
                    }
                    string id = null;
                    if (command.Length == 2)
                    {
                        id = command[1];
                    }
                    if (command[0].Equals("settings", StringComparison.OrdinalIgnoreCase))
                    {
                        filter(RpcHelper.settingNamespace, (string objid, byte[] protobuf) =>
                        {
                            if (id == null || id != null && id.Equals(objid))
                            {
                                Setting setting = Setting.Parser.ParseFrom(protobuf);
                                foreach (var entry in setting.Entries)
                                {
                                    Console.WriteLine($"{entry.Key}: {entry.Value}");
                                }
                            }
                        });
                    }
                    else if (command[0].Equals("wallets", StringComparison.OrdinalIgnoreCase))
                    {
                        filter(RpcHelper.creditCoinNamespace + RpcHelper.walletPrefix, (string objid, byte[] protobuf) =>
                        {
                            if (id == null || id != null && id.Equals(objid))
                            {
                                Wallet wallet = Wallet.Parser.ParseFrom(protobuf);
                                Console.WriteLine($"wallet({objid}) amount:{wallet.Amount}");
                            }
                        });
                    }
                    else if (command[0].Equals("addresses", StringComparison.OrdinalIgnoreCase))
                    {
                        filter(RpcHelper.creditCoinNamespace + RpcHelper.addressPrefix, (string objid, byte[] protobuf) =>
                        {
                            if (id == null || id != null && id.Equals(objid))
                            {
                                Address address = Address.Parser.ParseFrom(protobuf);
                                Console.WriteLine($"address({objid}) blockchain:{address.Blockchain} value:{address.Value} network:{address.Network} sighash:{address.Sighash}");
                            }
                        });
                    }
                    else if (command[0].Equals("transfers", StringComparison.OrdinalIgnoreCase))
                    {
                        filter(RpcHelper.creditCoinNamespace + RpcHelper.transferPrefix, (string objid, byte[] protobuf) =>
                        {
                            if (id == null || id != null && id.Equals(objid))
                            {
                                Transfer transfer = Transfer.Parser.ParseFrom(protobuf);
                                Console.WriteLine($"transfer({objid}) blockchain:{transfer.Blockchain} srcAddress:{transfer.SrcAddress} dstAddress:{transfer.DstAddress} order:{transfer.Order} amount:{transfer.Amount} tx:{transfer.Tx} block:{transfer.Block} processed:{transfer.Processed} sighash:{transfer.Sighash}");
                            }
                        });
                    }
                    else if (command[0].Equals("askOrders", StringComparison.OrdinalIgnoreCase))
                    {
                        filter(RpcHelper.creditCoinNamespace + RpcHelper.askOrderPrefix, (string objid, byte[] protobuf) =>
                        {
                            if (id == null || id != null && id.Equals(objid))
                            {
                                AskOrder askOrder = AskOrder.Parser.ParseFrom(protobuf);
                                Console.WriteLine($"askOrder({objid}) blockchain:{askOrder.Blockchain} address:{askOrder.Address} amount:{askOrder.Amount} interest:{askOrder.Interest} maturity:{askOrder.Maturity} fee:{askOrder.Fee} expiration:{askOrder.Expiration} block:{askOrder.Block} sighash:{askOrder.Sighash}");
                            }
                        });
                    }
                    else if (command[0].Equals("bidOrders", StringComparison.OrdinalIgnoreCase))
                    {
                        filter(RpcHelper.creditCoinNamespace + RpcHelper.bidOrderPrefix, (string objid, byte[] protobuf) =>
                        {
                            if (id == null || id != null && id.Equals(objid))
                            {
                                BidOrder bidOrder = BidOrder.Parser.ParseFrom(protobuf);
                                Console.WriteLine($"bidOrder({objid}) blockchain:{bidOrder.Blockchain} address:{bidOrder.Address} amount:{bidOrder.Amount} interest:{bidOrder.Interest} maturity:{bidOrder.Maturity} fee:{bidOrder.Fee} expiration:{bidOrder.Expiration} block:{bidOrder.Block} sighash:{bidOrder.Sighash}");
                            }
                        });
                    }
                    else if (command[0].Equals("offers", StringComparison.OrdinalIgnoreCase))
                    {
                        filter(RpcHelper.creditCoinNamespace + RpcHelper.offerPrefix, (string objid, byte[] protobuf) =>
                        {
                            if (id == null || id != null && id.Equals(objid))
                            {
                                Offer offer = Offer.Parser.ParseFrom(protobuf);
                                Console.WriteLine($"offer({objid}) blockchain:{offer.Blockchain} askOrder:{offer.AskOrder} bidOrder:{offer.BidOrder} expiration:{offer.Expiration} block:{offer.Block}");
                            }
                        });
                    }
                    else if (command[0].Equals("dealOrders", StringComparison.OrdinalIgnoreCase))
                    {
                        filter(RpcHelper.creditCoinNamespace + RpcHelper.dealOrderPrefix, (string objid, byte[] protobuf) =>
                        {
                            if (id == null || id != null && id.Equals(objid))
                            {
                                DealOrder dealOrder = DealOrder.Parser.ParseFrom(protobuf);
                                Console.WriteLine($"dealOrder({objid}) blockchain:{dealOrder.Blockchain} srcAddress:{dealOrder.SrcAddress} dstAddress:{dealOrder.DstAddress} amount:{dealOrder.Amount} interest:{dealOrder.Interest} maturity:{dealOrder.Maturity} fee:{dealOrder.Fee} expiration:{dealOrder.Expiration} block:{dealOrder.Block} loanTransfer:{(dealOrder.LoanTransfer.Equals(string.Empty) ? "*" : dealOrder.LoanTransfer)} repaymentTransfer:{(dealOrder.RepaymentTransfer.Equals(string.Empty) ? "*" : dealOrder.RepaymentTransfer)} lock:{(dealOrder.Lock.Equals(string.Empty) ? "*" : dealOrder.Lock)} sighash:{dealOrder.Sighash}");
                            }
                        });
                    }
                    else if (command[0].Equals("repaymentOrders", StringComparison.OrdinalIgnoreCase))
                    {
                        filter(RpcHelper.creditCoinNamespace + RpcHelper.repaymentOrderPrefix, (string objid, byte[] protobuf) =>
                        {
                            if (id == null || id != null && id.Equals(objid))
                            {
                                RepaymentOrder repaymentOrder = RepaymentOrder.Parser.ParseFrom(protobuf);
                                Console.WriteLine($"repaymentOrder({objid}) blockchain:{repaymentOrder.Blockchain} srcAddress:{repaymentOrder.SrcAddress} dstAddress:{repaymentOrder.DstAddress} amount:{repaymentOrder.Amount} expiration:{repaymentOrder.Expiration} block:{repaymentOrder.Block} deal:{repaymentOrder.Deal} previousOwner:{(repaymentOrder.PreviousOwner.Equals(string.Empty)? "*": repaymentOrder.PreviousOwner)} transfer:{(repaymentOrder.Transfer.Equals(string.Empty) ? "*" : repaymentOrder.Transfer)} sighash:{repaymentOrder.Sighash}");
                            }
                        });
                    }
                }
                else if (action.Equals("show"))
                {
                    bool success = true;

                    BigInteger headIdx = GetHeadIdx();

                    if (command.Length <= 1) throw new Exception("1 or more parametersd expected");
                    string sighash;
                    if (command[1].Equals("0"))
                    {
                        sighash = TxBuilder.getSighash(signer);
                    }
                    else
                    {
                        sighash = command[1];
                    }

                    if (command[0].Equals("balance", StringComparison.OrdinalIgnoreCase))
                    {
                        if (command.Length != 2) throw new Exception("2 parametersd expected");
                        string prefix = RpcHelper.creditCoinNamespace + RpcHelper.walletPrefix;
                        string id = prefix + sighash;
                        string amount = "0";
                        filter(prefix, (string objid, byte[] protobuf) =>
                        {
                            Wallet wallet = Wallet.Parser.ParseFrom(protobuf);
                            if (objid.Equals(id))
                            {
                                amount = wallet.Amount;
                            }
                        });
                        Console.WriteLine($"{amount}");
                    }
                    else if (command[0].Equals("address", StringComparison.OrdinalIgnoreCase))
                    {
                        if (command.Length != 5) throw new Exception("5 parametersd expected");
                        var blockchain = command[2].ToLower();
                        var addr = command[3];
                        var network = command[4].ToLower();
                        filter(RpcHelper.creditCoinNamespace + RpcHelper.addressPrefix, (string objid, byte[] protobuf) =>
                        {
                            Address address = Address.Parser.ParseFrom(protobuf);
                            if (address.Sighash == sighash && address.Blockchain == blockchain && address.Value == addr && address.Network == network)
                            {
                                Console.WriteLine(objid);
                            }
                        });
                    }
                    else if (command[0].Equals("matchingOrders", StringComparison.OrdinalIgnoreCase))
                    {
                        if (command.Length != 2) throw new Exception("2 parametersd expected");

                        var askOrders = new Dictionary<string, AskOrder>();
                        filter(RpcHelper.creditCoinNamespace + RpcHelper.askOrderPrefix, (string objid, byte[] protobuf) =>
                        {
                            AskOrder askOrder = AskOrder.Parser.ParseFrom(protobuf);
                            BigInteger block;
                            if (!BigInteger.TryParse(askOrder.Block, out block))
                            {
                                throw new Exception("Invalid numerics");
                            }
                            if (block + askOrder.Expiration > headIdx)
                            {
                                askOrders.Add(objid, askOrder);
                            }
                        });
                        var bidOrders = new Dictionary<string, BidOrder>();
                        filter(RpcHelper.creditCoinNamespace + RpcHelper.bidOrderPrefix, (string objid, byte[] protobuf) =>
                        {
                            BidOrder bidOrder = BidOrder.Parser.ParseFrom(protobuf);
                            BigInteger block;
                            if (!BigInteger.TryParse(bidOrder.Block, out block))
                            {
                                throw new Exception("Invalid numerics");
                            }
                            if (block + bidOrder.Expiration > headIdx)
                            {
                                bidOrders.Add(objid, bidOrder);
                            }
                        });

                        match(sighash, askOrders, bidOrders);
                    }
                    else if (command[0].Equals("currentOffers", StringComparison.OrdinalIgnoreCase))
                    {
                        if (command.Length != 2) throw new Exception("2 parametersd expected");

                        var bidOrders = new Dictionary<string, BidOrder>();
                        filter(RpcHelper.creditCoinNamespace + RpcHelper.bidOrderPrefix, (string objid, byte[] protobuf) =>
                        {
                            BidOrder bidOrder = BidOrder.Parser.ParseFrom(protobuf);
                            bidOrders.Add(objid, bidOrder);
                        });

                        filter(RpcHelper.creditCoinNamespace + RpcHelper.offerPrefix, (string objid, byte[] protobuf) =>
                        {
                            Offer offer = Offer.Parser.ParseFrom(protobuf);
                            BidOrder bidOrder = bidOrders[offer.BidOrder];
                            if (bidOrder.Sighash == sighash)
                            {
                                BigInteger block;
                                if (!BigInteger.TryParse(offer.Block, out block))
                                {
                                    throw new Exception("Invalid numerics");
                                }
                                if (block + offer.Expiration > headIdx)
                                {
                                    Console.WriteLine(objid);
                                }
                            }
                        });
                    }
                    else if (command[0].Equals("creditHistory", StringComparison.OrdinalIgnoreCase))
                    {
                        if (command.Length != 2) throw new Exception("2 parametersd expected");

                        filterDeals(null, sighash, (string dealAddress, DealOrder dealOrder) =>
                        {
                            var status = dealOrder.LoanTransfer.Equals(string.Empty) ? "NEW" : "COMPLETE";
                            if (!dealOrder.RepaymentTransfer.Equals(string.Empty))
                            {
                                status = "CLOSED";
                            }
                            Console.WriteLine($"status:{status}, amount:{dealOrder.Amount}, blockchain:{dealOrder.Blockchain}");
                        });
                    }
                    else if (command[0].Equals("newDeals", StringComparison.OrdinalIgnoreCase))
                    {
                        if (command.Length != 2) throw new Exception("2 parametersd expected");

                        filterDeals(sighash, null, (string dealAddress, DealOrder dealOrder) =>
                        {
                            if (dealOrder.LoanTransfer.Equals(string.Empty))
                            {
                                BigInteger block;
                                if (!BigInteger.TryParse(dealOrder.Block, out block))
                                {
                                    throw new Exception("Invalid numerics");
                                }
                                if (block + dealOrder.Expiration > headIdx)
                                {
                                    Console.WriteLine(dealAddress);
                                }
                            }
                        });
                    }
                    else if (command[0].Equals("transfer", StringComparison.OrdinalIgnoreCase))
                    {
                        if (command.Length != 3) throw new Exception("3 parametersd expected");
                        var orderId = command[2];

                        filter(RpcHelper.creditCoinNamespace + RpcHelper.transferPrefix, (string objid, byte[] protobuf) =>
                        {
                            Transfer transfer = Transfer.Parser.ParseFrom(protobuf);
                            if (transfer.Sighash == sighash && transfer.Order == orderId && !transfer.Processed)
                                Console.WriteLine(objid);
                        });
                    }
                    else if (command[0].Equals("currentLoans", StringComparison.OrdinalIgnoreCase))
                    {
                        if (command.Length != 2) throw new Exception("2 parametersd expected");

                        filterDeals(null, sighash, (string dealAddress, DealOrder dealOrder) =>
                        {
                            if (!dealOrder.LoanTransfer.Equals(string.Empty) && dealOrder.RepaymentTransfer.Equals(string.Empty))
                            {
                                Console.WriteLine(dealAddress);
                            }
                        });
                    }
                    else if (command[0].Equals("newRepaymentOrders", StringComparison.OrdinalIgnoreCase))
                    {
                        if (command.Length != 2) throw new Exception("2 parametersd expected");

                        var addresses = new Dictionary<string, Address>();
                        filter(RpcHelper.creditCoinNamespace + RpcHelper.addressPrefix, (string objid, byte[] protobuf) =>
                        {
                            Address address = Address.Parser.ParseFrom(protobuf);
                            addresses.Add(objid, address);
                        });

                        var dealOrders = new Dictionary<string, DealOrder>();
                        filter(RpcHelper.creditCoinNamespace + RpcHelper.dealOrderPrefix, (string objid, byte[] protobuf) =>
                        {
                            DealOrder dealOrder = DealOrder.Parser.ParseFrom(protobuf);
                            dealOrders.Add(objid, dealOrder);
                        });

                        filter(RpcHelper.creditCoinNamespace + RpcHelper.repaymentOrderPrefix, (string objid, byte[] protobuf) =>
                        {
                            RepaymentOrder repaymentOrder = RepaymentOrder.Parser.ParseFrom(protobuf);
                            DealOrder deal = dealOrders[repaymentOrder.Deal];
                            Address address = addresses[deal.SrcAddress];
                            if (repaymentOrder.Transfer.Equals(string.Empty) && repaymentOrder.PreviousOwner.Equals(string.Empty) && address.Sighash.Equals(sighash))
                            {
                                BigInteger block;
                                if (!BigInteger.TryParse(repaymentOrder.Block, out block))
                                {
                                    throw new Exception("Invalid numerics");
                                }
                                if (block + repaymentOrder.Expiration > headIdx)
                                {
                                    Console.WriteLine(objid);
                                }
                            }
                        });
                    }
                    else if (command[0].Equals("currentRepaymentOrders", StringComparison.OrdinalIgnoreCase))
                    {
                        filter(RpcHelper.creditCoinNamespace + RpcHelper.repaymentOrderPrefix, (string objid, byte[] protobuf) =>
                        {
                            RepaymentOrder repaymentOrder = RepaymentOrder.Parser.ParseFrom(protobuf);
                            if (repaymentOrder.Transfer.Equals(string.Empty) && !repaymentOrder.PreviousOwner.Equals(string.Empty) && repaymentOrder.Sighash.Equals(sighash))
                            {
                                Console.WriteLine(objid);
                            }
                        });
                    }
                    else
                    {
                        Console.WriteLine("Invalid command " + command[0]);
                        success = false;
                    }

                    if (success)
                    {
                        Console.WriteLine("Success");
                    }
                }
                else
                {
                    var txBuilder = new TxBuilder(signer);
                    if (action.Equals("creditcoin"))
                    {
                        string msg;
                        var tx = txBuilder.BuildTx(command, out msg);
                        if (tx == null)
                        {
                            Debug.Assert(msg != null);
                            Console.WriteLine(msg);
                        }
                        else
                        {
                            Debug.Assert(msg == null);
                            var content = new ByteArrayContent(tx);
                            content.Headers.Add("Content-Type", "application/octet-stream");
                            Console.WriteLine(RpcHelper.CompleteBatch(httpClient, creditcoinUrl, "batches", content, txid));
                        }
                    }
                    else
                    {
                        var loader = new Loader<ICCClientPlugin>();
                        var msgs = new List<string>();
                        loader.Load(pluginFolder, msgs);
                        foreach (var msg in msgs)
                        {
                            Console.WriteLine(msg);
                        }

                        ICCClientPlugin plugin = loader.Get(action);
                        var pluginConfig = config.GetSection(action);
                        if (plugin == null)
                        {
                            Console.WriteLine("Error: Unknown action " + action);
                        }
                        else
                        {
                            string msg;
                            var settings = getSettings();
                            bool done = plugin.Run(txid, pluginConfig, httpClient, txBuilder, settings, progressId, pluginFolder, creditcoinUrl, command, out inProgress, out msg);
                            if (done)
                            {
                                if (msg == null)
                                {
                                    msg = "Success";
                                }
                                Console.WriteLine(msg);
                            }
                            else
                            {
                                Console.WriteLine("Error: " + msg);
                            }
                        }
                    }
                }
                if (!inProgress)
                {
                    File.Delete(progress);
                }
            }
            catch (Exception x)
            {
                Console.WriteLine($"Error: unexpected failure - {x.Message}");
            }
        }

        private static BigInteger GetHeadIdx()
        {
            string msg;
            string head = RpcHelper.LastBlock(httpClient, creditcoinUrl, out msg);
            Debug.Assert(head != null && msg == null || head == null && msg != null);
            if (head == null)
            {
                throw new Exception(msg);
            }
            BigInteger headIdx;
            if (!BigInteger.TryParse(head, out headIdx))
            {
                throw new Exception("Invalid numerics");
            }

            return headIdx;
        }

        private static Dictionary<string, string> getSettings()
        {
            var settings = new Dictionary<string, string>();
            filter(RpcHelper.settingNamespace, (string address, byte[] protobuf) =>
            {
                Setting setting = Setting.Parser.ParseFrom(protobuf);
                foreach (var entry in setting.Entries)
                {
                    settings.Add(entry.Key, entry.Value);
                }
            });
            return settings;
        }

        private static Signer getSigner(IConfiguration config)
        {
            string signerHexStr = config["signer"];
            if (string.IsNullOrWhiteSpace(signerHexStr))
            {
                throw new Exception("Signer is not configured");
            }
            try
            {
                return new Signer(RpcHelper.HexToBytes(signerHexStr));
            }
            catch (Exception x)
            {
                throw new Exception("Failed to initialize signer: " + x.Message);
            }
        }

        private static void filterDeals(string investorSighash, string fundraiserSighash, Action<string, DealOrder> lister)
        {
            var dealOrders = new Dictionary<string, DealOrder>();
            filter(RpcHelper.creditCoinNamespace + RpcHelper.dealOrderPrefix, (string objid, byte[] protobuf) =>
            {
                DealOrder dealOrder = DealOrder.Parser.ParseFrom(protobuf);
                dealOrders.Add(objid, dealOrder);
            });
            var addresses = new Dictionary<string, Address>();
            filter(RpcHelper.creditCoinNamespace + RpcHelper.addressPrefix, (string objid, byte[] protobuf) =>
            {
                Address address = Address.Parser.ParseFrom(protobuf);
                addresses.Add(objid, address);
            });

            foreach (var dealOrderEntry in dealOrders)
            {
                DealOrder dealOrder = dealOrderEntry.Value;
                if (fundraiserSighash != null)
                {
                    Address address = addresses[dealOrder.DstAddress];
                    if (address.Sighash == fundraiserSighash)
                    {
                        lister(dealOrderEntry.Key, dealOrder);
                    }
                }
                if (investorSighash != null)
                {
                    Address address = addresses[dealOrder.SrcAddress];
                    if (address.Sighash == investorSighash)
                    {
                        lister(dealOrderEntry.Key, dealOrder);
                    }
                }
            }
        }

        private static void match(string sighash, Dictionary<string, AskOrder> askOrders, Dictionary<string, BidOrder> bidOrders)
        {
            foreach (var askOrderEntry in askOrders)
            {
                foreach (var bidOrderEntry in bidOrders)
                {
                    if (!askOrderEntry.Value.Sighash.Equals(sighash))
                    {
                        break;
                    }

                    BigInteger askAmount, bidAmount, askInterest, bidInterest, askMaturity, bidMaturity, askFee, bidFee;
                    if (!BigInteger.TryParse(askOrderEntry.Value.Amount, out askAmount) || !BigInteger.TryParse(bidOrderEntry.Value.Amount, out bidAmount) ||
                        !BigInteger.TryParse(askOrderEntry.Value.Interest, out askInterest) || !BigInteger.TryParse(bidOrderEntry.Value.Interest, out bidInterest) ||
                        !BigInteger.TryParse(askOrderEntry.Value.Maturity, out askMaturity) || !BigInteger.TryParse(bidOrderEntry.Value.Maturity, out bidMaturity) ||
                        !BigInteger.TryParse(askOrderEntry.Value.Fee, out askFee) || !BigInteger.TryParse(bidOrderEntry.Value.Fee, out bidFee))
                    {
                        Console.WriteLine("Invalid numerics");
                        return;
                    }

                    if (askAmount == bidAmount && askInterest / askMaturity <= bidInterest / bidMaturity && askFee <= bidFee)
                    {
                        Console.WriteLine($"{askOrderEntry.Key} {bidOrderEntry.Key}");
                    }
                }
            }
        }

        private static void filter(string prefix, Action<string, byte[]> lister)
        {
            var url = $"{creditcoinUrl}/state?address={prefix}";
            for (; ; )
            {
                using (HttpResponseMessage responseMessage = httpClient.GetAsync(url).Result)
                {
                    var json = responseMessage.Content.ReadAsStringAsync().Result;
                    var response = JObject.Parse(json);
                    if (response.ContainsKey(ERROR))
                    {
                        Console.WriteLine((string)response[ERROR][MESSAGE]);
                    }
                    else
                    {
                        Debug.Assert(response.ContainsKey(DATA));
                        var data = response[DATA];
                        foreach (var datum in data)
                        {
                            Debug.Assert(datum.Type == JTokenType.Object);
                            var obj = (JObject)datum;
                            Debug.Assert(obj.ContainsKey(ADDRESS));
                            var objid = (string)obj[ADDRESS];
                            Debug.Assert(obj.ContainsKey(DATA));
                            var content = (string)obj[DATA];
                            byte[] protobuf = Convert.FromBase64String(content);
                            lister(objid, protobuf);
                        }

                        Debug.Assert(response.ContainsKey(PAGING));
                        var paging = (JObject)response[PAGING];
                        if (!paging.ContainsKey(NEXT))
                            break;
                        url = (string)paging[NEXT];
                    }
                }
            }
        }
    }
}