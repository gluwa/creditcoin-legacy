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
using Sawtooth.Sdk;
using System.Security.Cryptography;
using System.Text;
using System.Numerics;
using NetMQ.Sockets;
using NetMQ;

namespace ccclient
{
    class Program
    {
        private static string creditcoinUrl = "http://localhost:8008";

        private static HttpClient httpClient = new HttpClient();

        private static string creditCoinNamespace = "8a1a04";
        private static string settingNamespace = "000000";
        private static string walletPrefix = "0000";
        private static string addressPrefix = "1000";
        private static string transferPrefix = "2000";
        private static string askOrderPrefix = "3000";
        private static string bidOrderPrefix = "4000";
        private static string dealOrderPrefix = "5000";
        private static string repaymentOrderPrefix = "6000";

        private const string ERROR = "error";
        private const string DATA = "data";
        private const string ADDRESS = "address";
        private const string MESSAGE = "message";

        private const int SKIP_TO_GET_60 = 512 / 8 * 2 - 60; // 512 - hash size, 8 - bits in byte, 2 - hex digits for byte, 60 - merkle address length (70) without namespace length (6) and prexix length (4)

        static void Main(string[] args)
        {
            try
            {
                IConfiguration config = new ConfigurationBuilder()
                    .AddJsonFile("appsettings.json", true, false)
#if DEBUG
                    .AddJsonFile("appsettings.dev.json", true, false)
#endif
                    .Build();
                string signerHexStr = config["signer"];
                if (string.IsNullOrWhiteSpace(signerHexStr))
                {
                    Console.WriteLine("Signer is not configured");
                    return;
                }
                var signer = new Signer(RpcHelper.HexToBytes(signerHexStr));

                string creditcoinRestApiURL = config["creditcoinRestApiURL"];
                if (!string.IsNullOrWhiteSpace(creditcoinRestApiURL))
                {
                    creditcoinUrl = creditcoinRestApiURL;
                }

                string root = Directory.GetCurrentDirectory();
                string folder = TxBuilder.GetPluginsFolder(root);
                if (folder == null)
                {
                    Console.WriteLine("plugins subfolder not found");
                    return;
                }

                string progress = Path.Combine(folder, "progress.txt");

                string action;
                string[] command;
                if (File.Exists(progress))
                {
                    Console.WriteLine("Found unfinished action, retrying...");
                    args = File.ReadAllText(progress).Split();
                }
                else
                {
                    File.WriteAllText(progress, string.Join(' ', args));
                }
                action = args[0].ToLower();
                command = args.Skip(1).ToArray();
                var txBuilder = new TxBuilder(signer);

                var settings = new Dictionary<string, string>();
                filter(settingNamespace, (string address, byte[] protobuf) =>
                {
                    Setting setting = Setting.Parser.ParseFrom(protobuf);
                    foreach (var entry in setting.Entries)
                    {
                        settings.Add(entry.Key, entry.Value);
                    }
                });

                bool inProgress = false;

                // API:
                // list Settings|Wallets|Addresses|Transfers|AskOrders|BidOrders|DealOrders|RepaymentOrders|
                // list Balance
                // list Sighash
                // list Address blockchain address
                // list UnusedTransfers addressId amount
                // list MatchingOrders
                // list CreditHistory sighash
                // list NewDeals
                // list RunningDeals
                // list NewRepaymentOrders
                // creditcoin AddFunds amount sighash (creditcoin AddFunds 1000000 16de574ac8ac3067977df056ecff51345672d25d528303b3555ab2aa4cd5)
                //      AddFunds only works for a registered signer
                // creditcoin SendFunds amount sighash (creditcoin SendFunds 200000 8704a4f77befea5c8082d414f98dc16e4ba82a0898422d031f41693260a0)
                // creditcoin RegisterAddress blockchain address (creditcoin RegisterAddress bitcoin <BITCOIN-ADDRESS>)
                // creditcoin AddAskOrder blockchain amount interest blockchain collateral fee expiration capitalLockTransferId (creditcoin AddAskOrder bitcoin 1000000 10 bitcoin 50 100 1565386152 <TRANSFER-ID>)
                // creditcoin AddBidOrder blockchain amount interest blockchain collateral fee expiration (creditcoin AddAskOrder bitcoin 1000000 10 bitcoin 50 100 1565386152)
                // creditcoin AddDealOrder askOrderId bidOrderId
                // creditcoin CompleteDealOrder dealOrderId collateralLockTransferId
                // creditcoin AddRepaymentOrder dealOrderId
                // creditcoin CompleteRepaymentOrder repaymentOrderId transferId
                // creditcoin CollectCoins transferId
                // <blockchain> RegisterTransfer ...
                //      bitcoin RegisterTransfer registeredAddressId amount sourceTxId
                //      ethereum RegisterTransfer registeredAddressId amount [erc20]
                // unlock Funds dealOrderId addressToUnlockFundsTo
                // unlock Collateral repaymentOrderId addressToUnlockCollateralTo

                if (action.Equals("unlock"))
                {
                    string externalGatewayAddress;
                    if (!settings.TryGetValue("sawtooth.validator.gateway", out externalGatewayAddress))
                    {
                        Console.WriteLine("Error: external gateway is not configured");
                    }
                    else
                    {
                        if (!externalGatewayAddress.StartsWith("tcp://"))
                        {
                            externalGatewayAddress = "tcp://" + externalGatewayAddress;
                        }
                        bool success = true;
                        switch (command[0].ToLower())
                        {
                            case "funds":
                                Debug.Assert(command.Length == 3);
                                {
                                    var dealOrderId = command[1].ToLower();
                                    var addressToUnlockFundsTo = command[2].ToLower();
                                    string msg;
                                    string blockchain = null;
                                    var protobuf = RpcHelper.ReadProtobuf(httpClient, $"{creditcoinUrl}/state/{dealOrderId}", out msg);
                                    if (protobuf != null)
                                    {
                                        var dealOrder = DealOrder.Parser.ParseFrom(protobuf);
                                        protobuf = RpcHelper.ReadProtobuf(httpClient, $"{creditcoinUrl}/state/{dealOrder.AskOrderId}", out msg);
                                        if (protobuf != null)
                                        {
                                            var askOrder = AskOrder.Parser.ParseFrom(protobuf);
                                            blockchain = askOrder.Blockchain;
                                        }
                                    }

                                    if (blockchain == null)
                                    {
                                        success = false;
                                        Console.WriteLine("Error: " + msg);
                                    }
                                    else
                                    {
                                        string escrow;
                                        if (!settings.TryGetValue("sawtooth.escrow." + blockchain, out escrow))
                                        {
                                            success = false;
                                            Console.WriteLine("Error: escrow is not configured for " + blockchain);
                                        }
                                        else
                                        {
                                            using (var socket = new RequestSocket())
                                            {
                                                socket.Connect(externalGatewayAddress);
                                                var request = $"{blockchain} unlock funds {escrow} {dealOrderId} {addressToUnlockFundsTo}";
                                                socket.SendFrame(request);
                                                string response = socket.ReceiveFrameString();
                                                if (response != "good")
                                                {
                                                    success = false;
                                                    Console.WriteLine("Error: failed to execute the global gateway command");
                                                }
                                            }
                                        }
                                    }
                                }
                                break;
                            case "collateral":
                                Debug.Assert(command.Length == 3);
                                {
                                    var repaymentOrderId = command[1].ToLower();
                                    var addressToUnlockCollateralsTo = command[2].ToLower();
                                    string msg;
                                    string blockchain = null;
                                    var protobuf = RpcHelper.ReadProtobuf(httpClient, $"{creditcoinUrl}/state/{repaymentOrderId}", out msg);
                                    if (protobuf != null)
                                    {
                                        var repaymentOrder = RepaymentOrder.Parser.ParseFrom(protobuf);
                                        protobuf = RpcHelper.ReadProtobuf(httpClient, $"{creditcoinUrl}/state/{repaymentOrder.DealId}", out msg);
                                        if (protobuf != null)
                                        {
                                            var dealOrder = DealOrder.Parser.ParseFrom(protobuf);
                                            protobuf = RpcHelper.ReadProtobuf(httpClient, $"{creditcoinUrl}/state/{dealOrder.AskOrderId}", out msg);
                                            if (protobuf != null)
                                            {
                                                var askOrder = AskOrder.Parser.ParseFrom(protobuf);
                                                blockchain = askOrder.Blockchain;
                                            }
                                        }
                                    }

                                    if (blockchain == null)
                                    {
                                        success = false;
                                        Console.WriteLine("Error: " + msg);
                                    }
                                    else
                                    {
                                        string escrow;
                                        if (!settings.TryGetValue("sawtooth.escrow." + blockchain, out escrow))
                                        {
                                            success = false;
                                            Console.WriteLine("Error: escrow is not configured for " + blockchain);
                                        }
                                        else
                                        {
                                            using (var socket = new RequestSocket())
                                            {
                                                socket.Connect(externalGatewayAddress);
                                                var request = $"{blockchain} unlock collateral {escrow} {repaymentOrderId} {addressToUnlockCollateralsTo}";
                                                socket.SendFrame(request);
                                                string response = socket.ReceiveFrameString();
                                                if (response != "good")
                                                {
                                                    success = false;
                                                    Console.WriteLine("Error: failed to execute the global gateway command");
                                                }
                                            }
                                        }
                                    }
                                }
                                break;
                        }
                        if (success)
                        {
                            Console.WriteLine("Success");
                        }
                    }
                }
                else if (action.Equals("list"))
                {
                    bool success = true;

                    if (command[0].Equals("settings", StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.Assert(command.Length == 1);
                        filter(settingNamespace, (string objid, byte[] protobuf) =>
                        {
                            Setting setting = Setting.Parser.ParseFrom(protobuf);
                            foreach (var entry in setting.Entries)
                            {
                                Console.WriteLine($"{entry.Key}: {entry.Value}");
                            }
                        });
                    }
                    else if (command[0].Equals("wallets", StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.Assert(command.Length == 1);
                        filter(creditCoinNamespace + walletPrefix, (string objid, byte[] protobuf) =>
                        {
                            Wallet wallet = Wallet.Parser.ParseFrom(protobuf);
                            Console.WriteLine($"wallet({objid}) amount:{wallet.Amount}");
                        });
                    }
                    else if (command[0].Equals("balance", StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.Assert(command.Length == 1);
                        {
                            string sighash = sha256(signer.GetPublicKey().ToHexString());
                            string prefix = creditCoinNamespace + walletPrefix;
                            string id = prefix + sighash;

                            filter(prefix, (string objid, byte[] protobuf) =>
                            {
                                Wallet wallet = Wallet.Parser.ParseFrom(protobuf);
                                if (objid.Equals(id))
                                {
                                    Console.WriteLine($"balance for {sighash} is {wallet.Amount}");
                                }
                            });
                        }
                    }
                    else if (command[0].Equals("addresses", StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.Assert(command.Length == 1);
                        filter(creditCoinNamespace + addressPrefix, (string objid, byte[] protobuf) =>
                        {
                            Address address = Address.Parser.ParseFrom(protobuf);
                            Console.WriteLine($"address({objid}) sighash:{address.Sighash}, blockchain: {address.Blockchain}, address:{address.Address_}");
                        });
                    }
                    else if (command[0].Equals("transfers", StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.Assert(command.Length == 1);
                        filter(creditCoinNamespace + transferPrefix, (string objid, byte[] protobuf) =>
                        {
                            Transfer transfer = Transfer.Parser.ParseFrom(protobuf);
                            Console.WriteLine($"transfer({objid}) sighash:{transfer.Sighash}, blockchain: {transfer.Blockchain}, amount:{transfer.Amount}, fee:{transfer.Fee}, txid:{transfer.Txid}");
                        });
                    }
                    else if (command[0].Equals("askOrders", StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.Assert(command.Length == 1);
                        filter(creditCoinNamespace + askOrderPrefix, (string objid, byte[] protobuf) =>
                        {
                            AskOrder askOrder = AskOrder.Parser.ParseFrom(protobuf);
                            Console.WriteLine($"askOrder({objid}) sighash:{askOrder.Sighash}, blockchain: {askOrder.Blockchain}, amount:{askOrder.Amount}, interest:{askOrder.Interest}, collateralBlockchain:{askOrder.CollateralBlockchain}, collateral:{askOrder.Collateral}, fee:{askOrder.Fee}, expiration:{askOrder.Expiration}, transferId:{askOrder.TransferId}");
                        });
                    }
                    else if (command[0].Equals("bidOrders", StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.Assert(command.Length == 1);
                        filter(creditCoinNamespace + bidOrderPrefix, (string objid, byte[] protobuf) =>
                        {
                            BidOrder bidOrder = BidOrder.Parser.ParseFrom(protobuf);
                            Console.WriteLine($"bidOrder({objid}) sighash:{bidOrder.Sighash}, blockchain: {bidOrder.Blockchain}, amount:{bidOrder.Amount}, interest:{bidOrder.Interest}, collateralBlockchain:{bidOrder.CollateralBlockchain}, collateral:{bidOrder.Collateral}, fee:{bidOrder.Fee}, expiration:{bidOrder.Expiration}");
                        });
                    }
                    else if (command[0].Equals("dealOrders", StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.Assert(command.Length == 1);
                        filter(creditCoinNamespace + dealOrderPrefix, (string objid, byte[] protobuf) =>
                        {
                            DealOrder dealOrder = DealOrder.Parser.ParseFrom(protobuf);
                            Console.WriteLine($"dealOrder({objid}) askOrderId:{dealOrder.AskOrderId}, bidOrderId: {dealOrder.BidOrderId}, collateralTransferId:{dealOrder.CollateralTransferId}");
                        });
                    }
                    else if (command[0].Equals("repaymentOrders", StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.Assert(command.Length == 1);
                        filter(creditCoinNamespace + repaymentOrderPrefix, (string objid, byte[] protobuf) =>
                        {
                            RepaymentOrder repaymentOrder = RepaymentOrder.Parser.ParseFrom(protobuf);
                            Console.WriteLine($"repaymentOrder({objid}) dealId:{repaymentOrder.DealId}, transferId:{repaymentOrder.TransferId}");
                        });
                    }
                    else if (command[0].Equals("sighash", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine(sha256(signer.GetPublicKey().ToHexString()));
                    }
                    else if (command[0].Equals("address", StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.Assert(command.Length == 3);
                        var blockchain = command[1].ToLower();
                        var addr = command[2];
                        filter(creditCoinNamespace + addressPrefix, (string objid, byte[] protobuf) =>
                        {
                            Address address = Address.Parser.ParseFrom(protobuf);
                            if (address.Blockchain == blockchain && address.Address_ == addr)
                            {
                                Console.WriteLine(objid);
                            }
                        });
                    }
                    else if (command[0].Equals("unusedTransfers", StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.Assert(command.Length == 3);
                        var addressId = command[1];
                        var amount = command[2];
                        var sighash = sha256(signer.GetPublicKey().ToHexString());

                        Address address = null;
                        filter(creditCoinNamespace + addressPrefix, (string objid, byte[] protobuf) =>
                        {
                            if (objid == addressId)
                            {
                                address = Address.Parser.ParseFrom(protobuf);
                            }
                        });
                        if (address == null)
                        {
                            Console.WriteLine("Invalid command " + command[0]);
                            success = false;
                        }
                        else
                        {
                            filter(creditCoinNamespace + transferPrefix, (string objid, byte[] protobuf) =>
                            {
                                Transfer transfer = Transfer.Parser.ParseFrom(protobuf);
                                if (transfer.Sighash == sighash && transfer.Orderid.Equals(string.Empty) && transfer.Blockchain == address.Blockchain && transfer.Amount == amount)
                                {
                                    Console.WriteLine(objid);
                                }
                            });
                        }
                    }
                    else if (command[0].Equals("matchingOrders", StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.Assert(command.Length == 1);

                        var askOrders = new Dictionary<string, AskOrder>();
                        filter(creditCoinNamespace + askOrderPrefix, (string objid, byte[] protobuf) =>
                        {
                            AskOrder askOrder = AskOrder.Parser.ParseFrom(protobuf);
                            askOrders.Add(objid, askOrder);
                        });
                        var bidOrders = new Dictionary<string, BidOrder>();
                        filter(creditCoinNamespace + bidOrderPrefix, (string objid, byte[] protobuf) =>
                        {
                            BidOrder bidOrder = BidOrder.Parser.ParseFrom(protobuf);
                            bidOrders.Add(objid, bidOrder);
                        });

                        match(signer, askOrders, bidOrders);
                    }
                    else if (command[0].Equals("creditHistory", StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.Assert(command.Length == 2);

                        var fundraiser = command[1].ToLower();

                        filterDeals(null, fundraiser, (string dealAddress, DealOrder dealOrder, AskOrder askOrder, BidOrder bidOrder) =>
                        {
                            Debug.Assert(askOrder == null);
                            var status = dealOrder.CollateralTransferId.Equals(string.Empty) ? "INCOMPLETE" : "COMPLETE";
                            if (!dealOrder.UnlockCollateralDestinationAddressId.Equals(string.Empty))
                            {
                                status = "CLOSED";
                            }
                            else if (!dealOrder.UnlockFundsDestinationAddressId.Equals(string.Empty))
                            {
                                status = "ACTIVE";
                            }
                            Console.WriteLine($"status: {status}, amount:{bidOrder.Amount}, blockchain: {bidOrder.Blockchain}, collateral:{bidOrder.Collateral}");
                        });
                    }
                    else if (command[0].Equals("newDeals", StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.Assert(command.Length == 1);

                        var fundraiser = sha256(signer.GetPublicKey().ToHexString());

                        filterDeals(null, fundraiser, (string dealAddress, DealOrder dealOrder, AskOrder askOrder, BidOrder bidOrder) =>
                        {
                            Debug.Assert(askOrder == null);
                            if (dealOrder.CollateralTransferId.Equals(string.Empty))
                            {
                                Console.WriteLine(dealAddress);
                            }
                        });
                    }
                    else if (command[0].Equals("runningDeals", StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.Assert(command.Length == 1);

                        var investor = sha256(signer.GetPublicKey().ToHexString());

                        var deals = new List<string>();
                        filterDeals(investor, null, (string dealAddress, DealOrder dealOrder, AskOrder askOrder, BidOrder bidOrder) =>
                        {
                            Debug.Assert(bidOrder == null);
                            if (!dealOrder.CollateralTransferId.Equals(string.Empty) && dealOrder.UnlockCollateralDestinationAddressId.Equals(string.Empty))
                            {
                                deals.Add(dealAddress);
                            }
                        });

                        filter(creditCoinNamespace + repaymentOrderPrefix, (string objid, byte[] protobuf) =>
                        {
                            RepaymentOrder repaymentOrder = RepaymentOrder.Parser.ParseFrom(protobuf);
                            if (deals.SingleOrDefault(x => x.Equals(repaymentOrder.DealId)) != null)
                            {
                                deals.Remove(repaymentOrder.DealId);
                            }
                        });

                        foreach (var deal in deals)
                        {
                            Console.WriteLine(deal);
                        }
                    }
                    else if (command[0].Equals("newRepaymentOrders", StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.Assert(command.Length == 1);

                        var fundraiser = sha256(signer.GetPublicKey().ToHexString());

                        var deals = new List<string>();
                        filterDeals(null, fundraiser, (string dealAddress, DealOrder dealOrder, AskOrder askOrder, BidOrder bidOrder) =>
                        {
                            Debug.Assert(askOrder == null);
                            if (!dealOrder.CollateralTransferId.Equals(string.Empty) && dealOrder.UnlockCollateralDestinationAddressId.Equals(string.Empty))
                            {
                                deals.Add(dealAddress);
                            }
                        });

                        filter(creditCoinNamespace + repaymentOrderPrefix, (string objid, byte[] protobuf) =>
                        {
                            RepaymentOrder repaymentOrder = RepaymentOrder.Parser.ParseFrom(protobuf);
                            if (repaymentOrder.TransferId.Equals(string.Empty) && deals.SingleOrDefault(x => x.Equals(repaymentOrder.DealId)) != null)
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
                else if (action.Equals("creditcoin"))
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
                        Console.WriteLine(RpcHelper.CompleteBatch(httpClient, $"{creditcoinUrl}/batches", content));
                    }
                }
                else
                {
                    var loader = new Loader<ICCClientPlugin>();
                    var msgs = new List<string>();
                    loader.Load(folder, msgs);
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
                        bool done = plugin.Run(pluginConfig, httpClient, txBuilder, settings, folder, creditcoinUrl, command, out inProgress, out msg);
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

        private static string sha256(string message)
        {
            var data = Encoding.UTF8.GetBytes(message);
            using (SHA512 sha512 = new SHA512Managed())
            {
                var hash = sha512.ComputeHash(data);
                var hashString = String.Concat(Array.ConvertAll(hash, x => x.ToString("X2")));
                return hashString.Substring(SKIP_TO_GET_60).ToLower();
            }
        }

        private static void filterDeals(string investorSighash, string fundraiserSighash, Action<string, DealOrder, AskOrder, BidOrder> lister)
        {
            var dealOrders = new Dictionary<string, DealOrder>();
            filter(creditCoinNamespace + dealOrderPrefix, (string objid, byte[] protobuf) =>
            {
                DealOrder dealOrder = DealOrder.Parser.ParseFrom(protobuf);
                dealOrders.Add(objid, dealOrder);
            });
            var bidOrders = new Dictionary<string, BidOrder>();
            if (fundraiserSighash != null)
            {
                filter(creditCoinNamespace + bidOrderPrefix, (string objid, byte[] protobuf) =>
                {
                    BidOrder bidOrder = BidOrder.Parser.ParseFrom(protobuf);
                    bidOrders.Add(objid, bidOrder);
                });
            }
            var askOrders = new Dictionary<string, AskOrder>();
            if (investorSighash != null)
            {
                filter(creditCoinNamespace + askOrderPrefix, (string objid, byte[] protobuf) =>
                {
                    AskOrder askOrder = AskOrder.Parser.ParseFrom(protobuf);
                    askOrders.Add(objid, askOrder);
                });
            }

            foreach (var dealOrderEntry in dealOrders)
            {
                DealOrder dealOrder = dealOrderEntry.Value;
                if (fundraiserSighash != null)
                {
                    BidOrder bidOrder = bidOrders[dealOrder.BidOrderId];
                    if (bidOrder.Sighash == fundraiserSighash)
                    {
                        lister(dealOrderEntry.Key, dealOrder, null, bidOrder);
                    }
                }
                if (investorSighash != null)
                {
                    AskOrder askOrder = askOrders[dealOrder.AskOrderId];
                    if (askOrder.Sighash == investorSighash)
                    {
                        lister(dealOrderEntry.Key, dealOrder, askOrder, null);
                    }
                }
            }
        }

        private static void match(Signer signer, Dictionary<string, AskOrder> askOrders, Dictionary<string, BidOrder> bidOrders)
        {
            var sighash = sha256(signer.GetPublicKey().ToHexString());
            foreach (var askOrderEntry in askOrders)
            {
                foreach (var bidOrderEntry in bidOrders)
                {
                    if (!askOrderEntry.Value.Sighash.Equals(sighash))
                    {
                        break;
                    }

                    BigInteger askAmount, bidAmount, askInterest, bidInterest, askCollateral, bidCollateral, askFee, bidFee;
                    if (!BigInteger.TryParse(askOrderEntry.Value.Amount, out askAmount) || !BigInteger.TryParse(bidOrderEntry.Value.Amount, out bidAmount) ||
                        !BigInteger.TryParse(askOrderEntry.Value.Interest, out askInterest) || !BigInteger.TryParse(bidOrderEntry.Value.Interest, out bidInterest) ||
                        !BigInteger.TryParse(askOrderEntry.Value.Collateral, out askCollateral) || !BigInteger.TryParse(bidOrderEntry.Value.Collateral, out bidCollateral) ||
                        !BigInteger.TryParse(askOrderEntry.Value.Fee, out askFee) || !BigInteger.TryParse(bidOrderEntry.Value.Fee, out bidFee))
                    {
                        Console.WriteLine("Invalid numerics");
                        return;
                    }

                    if (askAmount == bidAmount && askInterest <= bidInterest && askCollateral <= bidCollateral && askFee <= bidFee)
                    {
                        Console.WriteLine($"{askOrderEntry.Key}/{bidOrderEntry.Key}");
                    }
                }
            }
        }

        private static void filter(string prefix, Action<string, byte[]> lister)
        {
            using (HttpResponseMessage responseMessage = httpClient.GetAsync($"{creditcoinUrl}/state?address={prefix}").Result)
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
                }
            }
        }
    }
}