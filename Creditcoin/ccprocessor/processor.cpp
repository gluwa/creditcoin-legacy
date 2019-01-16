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

// processor.cpp : Defines the entry point for the console application.
//

#include "stdafx.h"

#include <string>
#include <iostream>

#include <cryptopp/sha.h>
#include <cryptopp/filters.h>
#include <cryptopp/hex.h>
#include <cryptopp/hrtimer.h>

#include <sawtooth_sdk.h>
#include <exceptions.h>
#include <setting.pb.h>

#include <log4cxx/basicconfigurator.h>
#include <nlohmann/json/json.hpp>
#include <zmqpp/context.hpp>
#include <zmqpp/socket.hpp>
#include <zmqpp/socket_types.hpp>

#include <boost/algorithm/string.hpp>
#include <boost/network/protocol/http/client.hpp>
#include <boost/multiprecision/cpp_int.hpp>

#include "Address.pb.h"
#include "AskOrder.pb.h"
#include "BidOrder.pb.h"
#include "DealOrder.pb.h"
#include "Pledge.pb.h"
#include "RepaymentOrder.pb.h"
#include "Transfer.pb.h"
#include "Wallet.pb.h"

const int URL_PREFIX_LEN = 6;
const size_t MERKLE_ADDRESS_LENGTH = 70;
const size_t NAMESPACE_PREFIX_LENGTH = 6;
const size_t PREFIX_LENGTH = 4;
const int SKIP_TO_GET_60 = 512 / 8 * 2 - 60; // 512 - hash size in bits, 8 - bits in byte, 2 - hex digits for byte, 60 - merkle address length (70) without namespace length (6) and prexix length (4)

const char* URL_PREFIX = "tcp://";
static std::string URL_GATEWAY = "tcp://127.0.0.1:55555";
static std::string URL_REST_API = "http://localhost:8008";
static std::string URL_VALIDATOR = "tcp://127.0.0.1:4004";
static const std::string TRANSFERS_ROOT = "TRANSFERS_ROOT";
static const std::string NAMESPACE = "CREDITCOIN";
static const std::string SETTINGS_NAMESPACE = "000000";

static const char WALLET[] = "0000";
//static_assert(sizeof(WALLET) / sizeof(char) - 1 == PREFIX_LENGTH);
static const char SOURCE[] = "1000";
//static_assert(sizeof(SOURCE) / sizeof(char) - 1 == PREFIX_LENGTH);
static const char TRANSFER[] = "2000";
//static_assert(sizeof(TRANSFER) / sizeof(char) - 1 == PREFIX_LENGTH);
static const char ASK_ORDER[] = "3000";
//static_assert(sizeof(ASK_ORDER) / sizeof(char) - 1 == PREFIX_LENGTH);
static const char BID_ORDER[] = "4000";
//static_assert(sizeof(BID_ORDER) / sizeof(char) - 1 == PREFIX_LENGTH);
static const char DEAL_ORDER[] = "5000";
//static_assert(sizeof(DEAL_ORDER) / sizeof(char) - 1 == PREFIX_LENGTH);
static const char REPAYMENT_ORDER[] = "6000";
//static_assert(sizeof(REPAYMENT_ORDER) / sizeof(char) - 1 == PREFIX_LENGTH);

static char const* RPC_FAILURE = "Failed to process RPC response";
static char const* DATA = "data";
static char const* ADDRESS = "address";
static char const* ERR = "error";

static const int SOCKET_TIMEOUT_MILLISECONDS = 5000000; // TODO make configurable or set a more reasonable time for prod env
static const int LOCAL_SOCKET_TIMEOUT_MILLISECONDS = 5000; // TODO re-evaluate the need for this after gateway changes

static std::map<std::string, std::string> settings;
static std::string externalGatewayAddress = "";
static zmqpp::socket* localGateway;
static zmqpp::socket* externalGateway;
static zmqpp::context* socketContext;

static void usage(int exitCode = 1)
{
    std::cout << "Usage:" << std::endl;
    std::cout << "processor [connect_string]" << std::endl;
    std::cout << "    connect_string - connect string to validator in format tcp://host:port" << std::endl;
    exit(exitCode);
}

static bool testConnectString(const char* str)
{
    const char* ptr = str;

    if (strncmp(str, URL_PREFIX, URL_PREFIX_LEN))
    {
        return false;
    }

    ptr = str + URL_PREFIX_LEN;

    if (!isdigit(*ptr))
    {
        if (*ptr == ':' || (ptr = strchr(ptr, ':')) == NULL)
        {
            return false;
        }
        ptr++;
    }
    else
    {
        for (int i = 0; i < 4; i++)
        {
            if (!isdigit(*ptr))
            {
                return false;
            }

            ptr++;
            if (isdigit(*ptr))
            {
                ptr++;
                if (isdigit(*ptr))
                {
                    ptr++;
                }
            }

            if (i < 3)
            {
                if (*ptr != '.')
                {
                    return false;
                }
            }
            else
            {
                if (*ptr != ':')
                {
                    return false;
                }
            }
            ptr++;
        }
    }

    for (int i = 0; i < 4; i++)
    {
        if (!isdigit(*ptr))
        {
            if (!i)
            {
                return false;
            }
            break;
        }
        ptr++;
    }

    if (*ptr)
    {
        return false;
    }

    return true;
}

static void parseArgs(int argc, char** argv)
{
    if (argc >= 2)
    {
		/* tmp hax, remove url test to support docker endpoints
		if (!testConnectString(argv[1]))
		{
			std::cerr << "Connect string is not in format host:port - " << argv[1] << std::endl;
			usage();
		}
		*/
		URL_VALIDATOR = argv[1];
    }
	std::cout << "Connecting to " << URL_VALIDATOR << std::endl;

	if (argc >= 3)
	{
		URL_GATEWAY = argv[2];
	}
	std::cout << "Using gateway URL: " << URL_GATEWAY << std::endl;

	if (argc >= 4)
	{
		URL_REST_API = argv[3];
	}
	std::cout << "Using rest api URL: " << URL_REST_API << std::endl;
}

class AddressFormatError : public std::runtime_error
{
public:
    explicit AddressFormatError(std::string const& error) : std::runtime_error(error)
    {
    }
};

static std::string sha512(const std::string& message)
{
    std::string digest;
    CryptoPP::SHA512 hash;

    CryptoPP::StringSource hasher(
        message,
        true,
        new CryptoPP::HashFilter(
            hash,
            new CryptoPP::HexEncoder(
                new CryptoPP::StringSink(digest),
                false)));

    return digest;
}

static std::string sha512id(const std::string& message)
{
    std::string ret = sha512(message).substr(SKIP_TO_GET_60, std::string::npos);
    assert(ret.length() == MERKLE_ADDRESS_LENGTH - NAMESPACE_PREFIX_LENGTH - PREFIX_LENGTH);
    return ret;
}

static std::string mapNamespace(std::string const& namespaceString)
{
    std::string ns = sha512(namespaceString);
    return ns.substr(0, NAMESPACE_PREFIX_LENGTH);
}

static std::string namespacePrefix = mapNamespace(NAMESPACE);

static bool isHex(const std::string& str)
{
    return str.find_first_not_of("0123456789abcdef") == std::string::npos;
}

static std::string makeAddress(std::string const& prefix, std::string const& key)
{
    std::string id = sha512id(key);
    std::string addr = namespacePrefix + prefix + id;
    assert(addr.length() == MERKLE_ADDRESS_LENGTH);
    return addr;
}

static const char* base64lookupString = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

static std::vector<int> MakeBase64lookupTable()
{
    std::vector<int> ret(256, -1);
    for (int i = 0; i < 64; i++)
    {
        ret[base64lookupString[i]] = i;
    }
    return ret;
}
std::vector<int> base64lookupTable = MakeBase64lookupTable();

static std::string encodeBase64(std::vector<std::uint8_t> const& in)
{
    std::stringstream out;

    unsigned int val = 0;
    int valb = -6;
    for (unsigned char c : in)
    {
        val = (val << 8) + c;
        valb += 8;
        while (valb >= 0)
        {
            out << (base64lookupString[(val >> valb) & 0x3F]);
            valb -= 6;
        }
    }
    if (valb > -6)
    {
        out << (base64lookupString[((val << 8) >> (valb + 8)) & 0x3F]);
    }
    for (int i = 0, rest = in.size() % 3; i < rest; ++i)
    {
        out << '=';
    }
    return out.str();
}

static std::vector<std::uint8_t> decodeBase64(std::string const& in)
{
    std::vector<std::uint8_t> ret;
    unsigned int val = 0;
    int valb = -8;
    for (unsigned char c : in)
    {
        if (c == '=')
        {
            break;
        }
        int lookup = base64lookupTable[c];
        if (lookup == -1)
        {
            throw sawtooth::InvalidTransaction("Invalid character in base64");
        }
        val = (val << 6) + lookup;
        valb += 6;
        if (valb >= 0)
        {
            ret.push_back(char((val >> valb) & 0xFF));
            valb -= 8;
        }
    }
    return ret;
}

static std::string toString(std::vector<std::uint8_t> const& v)
{
    const char* data = reinterpret_cast<const char*>(v.data());
    std::string out(data, data + v.size());
    return out;
}

static std::vector<std::uint8_t> toVector(const std::string& in)
{
    std::vector<std::uint8_t> out(in.begin(), in.end());
    return out;
}

static std::string trimQuotes(std::string quotedStr)
{
    auto quotedStrLen = quotedStr.length();
    assert(quotedStr[0] == '"' && quotedStr[quotedStrLen - 1] == '"');
    return quotedStr.substr(1, quotedStrLen - 2);
}

static unsigned long parseUlong(std::string numberString) {
    unsigned long number;
    bool error = false;
    try
    {
        size_t pos = 0;
        number = std::stoul(numberString, &pos, 10);
        if (pos < numberString.length())
        {
            error = true;
        }
    }
    catch (...)
    {
        error = true;
    }
    if (error)
    {
        throw sawtooth::InvalidTransaction("Invalid number");
    }

    return number;
}

static std::string getParam(nlohmann::json const& query, std::string const& id, std::string const& name)
{
    auto param = query.find(id);
    if (param == query.end())
    {
        throw sawtooth::InvalidTransaction("Expecting " + name);
    }
    return param->dump();
}

static std::string getString(nlohmann::json const& query, std::string const& id, std::string const& name)
{
    return trimQuotes(getParam(query, id, name));
}

static std::string getStringLower(nlohmann::json const& query, std::string const& id, std::string const& name)
{
    std::string ret = getString(query, id, name);
    boost::to_lower(ret);
    return ret;
}

static unsigned long getUlong(nlohmann::json const& query, std::string const& id, std::string const& name)
{
    return parseUlong(getString(query, id, name));
}

static boost::multiprecision::cpp_int getBigint(std::string bigint)
{
    boost::multiprecision::cpp_int ret;
    try 
    {
        ret = boost::multiprecision::cpp_int(bigint);
        if (ret < 0)
        {
            throw sawtooth::InvalidTransaction("Expecting a positive value");
        }
    }
    catch (std::runtime_error const&)
    {
        throw sawtooth::InvalidTransaction("Invalid number format");
    }
    return ret;
}

static std::string getBigint(nlohmann::json const& query, std::string const& id, std::string const& name, boost::multiprecision::cpp_int* value = 0)
{
    std::string bigint = getString(query, id, name);
    boost::multiprecision::cpp_int result = getBigint(bigint);
    if (value)
    {
        *value = result;
    }
    return bigint;
}

static unsigned long getDate(nlohmann::json const& query, std::string const& id, std::string const& name)
{
    unsigned long date = getUlong(query, id, name);
    time_t localTime;
    time(&localTime);
    tm* timeInfo = gmtime(&localTime);
    time_t utcTime = mktime(timeInfo);
    if (utcTime >= date)
    {
        throw std::runtime_error("Expired");
    }
    return date;
}

static std::string toString(boost::multiprecision::cpp_int const& bigint)
{
    std::stringstream ss;
    ss << bigint;
    return ss.str();
}

static void filter(std::string prefix, std::function<void(std::string const&, std::vector<std::uint8_t> const&)> const& lister)
{
    using namespace boost::network;
    http::client client;

    try
    {
        http::client::request request(URL_REST_API + "/state?address=" + prefix);
        request << header("Connection", "close");
        std::string json = body(client.get(request));
        nlohmann::json response = nlohmann::json::parse(json);
        if (response.count(ERR) || response.count(DATA) != 1)
        {
            throw sawtooth::InvalidTransaction(RPC_FAILURE);
        }
        auto data = response[DATA];
        if (!data.is_array())
        {
            throw sawtooth::InvalidTransaction(RPC_FAILURE);
        }
        auto arr = data.get<std::vector<nlohmann::json::value_type>>();
        for (auto datum : arr)
        {
            if (datum.count(ADDRESS) != 1 || datum.count(DATA) != 1)
            {
                throw sawtooth::InvalidTransaction(RPC_FAILURE);
            }
            std::string address = datum[ADDRESS];
            std::string content = datum[DATA];
            auto protobuf = decodeBase64(content);
            lister(address, protobuf);
        }
    }
    catch (sawtooth::InvalidTransaction const&)
    {
        throw;
    }
    catch (...)
    {
        throw sawtooth::InvalidTransaction(RPC_FAILURE);
    }
}

class Applicator : public sawtooth::TransactionApplicator
{
public:
    Applicator(sawtooth::TransactionUPtr txn, sawtooth::GlobalStateUPtr state) :
        TransactionApplicator(std::move(txn), std::move(state))
    {
    };

    void Apply()
    {
        std::cout << "Applicator::Apply" << std::endl;

        std::string cmd;
        auto query = cborToParams(&cmd);

        if (boost::iequals(cmd, "AddFunds"))
        {
            AddFunds(query);
        }
        else if (boost::iequals(cmd, "SendFunds"))
        {
            SendFunds(query);
        }
        else if (boost::iequals(cmd, "RegisterAddress"))
        {
            RegisterAddress(query);
        }
        else if (boost::iequals(cmd, "RegisterTransfer"))
        {
            RegisterTransfer(query);
        }
        else if (boost::iequals(cmd, "AddAskOrder"))
        {
            AddAskOrder(query);
        }
        else if (boost::iequals(cmd, "AddBidOrder"))
        {
            AddBidOrder(query);
        }
        else if (boost::iequals(cmd, "AddDealOrder"))
        {
            AddDealOrder(query);
        }
        else if (boost::iequals(cmd, "CompleteDealOrder"))
        {
            CompleteDealOrder(query);
        }
        else if (boost::iequals(cmd, "AddRepaymentOrder"))
        {
            AddRepaymentOrder(query);
        }
        else if (boost::iequals(cmd, "CompleteRepaymentOrder"))
        {
            CompleteRepaymentOrder(query);
        }
        else if (boost::iequals(cmd, "UnlockFunds"))
        {
            UnlockFunds(query);
        }
        else if (boost::iequals(cmd, "UnlockCollateral"))
        {
            UnlockCollateral(query);
        }
        else if (boost::iequals(cmd, "CollectCoins"))
        {
            CollectCoins(query);
        }
        else
        {
            std::stringstream error;
            error << "invalid command: '" << cmd << "'";
            throw sawtooth::InvalidTransaction(error.str());
        }
    }

private:
    nlohmann::json cborToParams(std::string* cmd)
    {
        const std::string& rawData = txn->payload();
        std::vector<uint8_t> dataVector = toVector(rawData);
        nlohmann::json query = nlohmann::json::from_cbor(dataVector);

        if (!query.is_object())
        {
            throw sawtooth::InvalidTransaction("CBOR Object as the encoded command");
        }

        auto verb = query.find("v");
        if (verb == query.end())
        {
            throw sawtooth::InvalidTransaction("verb is required");
        }
        *cmd = trimQuotes(verb->dump());

        return query;
    };

    std::string getStateData(std::string id, bool existing = false)
    {
        std::string stateData;
        if (!state->GetState(&stateData, id))
        {
            throw sawtooth::InvalidTransaction("Failed to retrieve the state");
        }
        if (existing && stateData.empty())
        {
            throw sawtooth::InvalidTransaction("Existing state expected");
        }
        return stateData;
    }

    void verifyGatewaySigner()
    {
        const std::string signer = txn->header()->GetValue(sawtooth::TransactionHeaderField::TransactionHeaderSignerPublicKey);
        const std::string mySighash = sha512id(signer);
        auto setting = settings.find("sawtooth.gateway.sighash");
        if (setting == settings.end())
        {
            throw sawtooth::InvalidTransaction("Gateway sighash is not configured");
        }
        if (mySighash != setting->second)
        {
            throw sawtooth::InvalidTransaction("Only gateway sighash can perform this operation");
        }
    }

    void addState(std::vector<sawtooth::GlobalState::KeyValue>* states, std::string id, google::protobuf::Message const& message)
    {
        std::string data;
        message.SerializeToString(&data);
        states->push_back(sawtooth::GlobalState::KeyValue(id, data));
    }

private:
    void AddFunds(nlohmann::json const& query)
    {
        boost::multiprecision::cpp_int amount;
        const std::string amountString = getBigint(query, "p1", "amount", &amount);
        const std::string sighash = getStringLower(query, "p2", "sighash");

        verifyGatewaySigner();

        const std::string walletId = namespacePrefix + WALLET + sighash;

        std::string stateData = getStateData(walletId);
        Wallet wallet;
        if (stateData.empty())
        {
            wallet.set_amount(amountString);
        }
        else
        {
            wallet.ParseFromString(stateData);
            boost::multiprecision::cpp_int balance = getBigint(wallet.amount());
            balance += amount;
            wallet.set_amount(toString(balance));
        }

        wallet.SerializeToString(&stateData);

        std::vector<sawtooth::GlobalState::KeyValue> states;
        states.push_back(sawtooth::GlobalState::KeyValue(walletId, stateData));
        state->SetState(states);
    }

    void SendFunds(nlohmann::json const& query)
    {
        boost::multiprecision::cpp_int amount;
        const std::string amountString = getBigint(query, "p1", "amount", &amount);
        const std::string sighash = getStringLower(query, "p2", "sighash");

        const std::string signer = txn->header()->GetValue(sawtooth::TransactionHeaderField::TransactionHeaderSignerPublicKey);
        const std::string mySighash = sha512id(signer);
        if (sighash == mySighash)
        {
            throw sawtooth::InvalidTransaction("Invalid destination");
        }

        const std::string srcWalletId = namespacePrefix + WALLET + mySighash;
        std::string stateData = getStateData(srcWalletId, true);

        Wallet srcWallet;
        srcWallet.ParseFromString(stateData);
        boost::multiprecision::cpp_int srcBalance = getBigint(srcWallet.amount());
        if (srcBalance - amount < 0)
        {
            throw sawtooth::InvalidTransaction("Insufficient funds");
        }

        srcBalance -= amount;
        srcWallet.set_amount(toString(srcBalance));

        const std::string dstWalletId = namespacePrefix + WALLET + sighash;
        stateData = getStateData(dstWalletId);

        Wallet dstWallet;
        if (stateData.empty())
        {
            dstWallet.set_amount(amountString);
        }
        else
        {
            dstWallet.ParseFromString(stateData);
            boost::multiprecision::cpp_int dstBalance = getBigint(dstWallet.amount());
            dstBalance += amount;
            dstWallet.set_amount(toString(dstBalance));
        }

        std::vector<sawtooth::GlobalState::KeyValue> states;
        addState(&states, srcWalletId, srcWallet);
        addState(&states, dstWalletId, dstWallet);
        state->SetState(states);
    }

    void RegisterAddress(nlohmann::json const& query)
    {
        const std::string blockchain = getStringLower(query, "p1", "blockchain");
        const std::string addressString = getString(query, "p2", "address");
        const std::string id = makeAddress(SOURCE, blockchain + addressString);

        std::string stateData = getStateData(id);
        if (!stateData.empty())
        {
            throw sawtooth::InvalidTransaction("The address has been already registered");
        }

        const std::string signer = txn->header()->GetValue(sawtooth::TransactionHeaderField::TransactionHeaderSignerPublicKey);

        Address address;
        address.set_blockchain(blockchain);
        address.set_address(addressString);
        address.set_sighash(sha512id(signer));

        std::vector<sawtooth::GlobalState::KeyValue> states;
        addState(&states, id, address);
        state->SetState(states);
    }

    void RegisterTransfer(nlohmann::json const& query)
    {
        const std::string addressId = getStringLower(query, "p1", "addressId");
        boost::multiprecision::cpp_int amount;
        const std::string amountString = getBigint(query, "p2", "amount", &amount);
        boost::multiprecision::cpp_int fee;
        const std::string feeString = getBigint(query, "p3", "fee", &fee);
        const std::string blockchainTxId = getStringLower(query, "p4", "blockchainTxId");
        const std::string networkId = getStringLower(query, "p5", "networkId");

        std::string stateData = getStateData(addressId, true);
        Address address;
        address.ParseFromString(stateData);
        std::string signer = txn->header()->GetValue(sawtooth::TransactionHeaderField::TransactionHeaderSignerPublicKey);
        std::string sighash = sha512id(signer);
        if (address.sighash() != sighash)
        {
            throw sawtooth::InvalidTransaction("Only the owner can register");
        }
        const std::string blockchain = address.blockchain();

        const std::string transferId = makeAddress(TRANSFER, blockchain + blockchainTxId);
        stateData = getStateData(transferId);
        if (!stateData.empty())
        {
            throw sawtooth::InvalidTransaction("The transfer has been already registered");
        }

        std::stringstream gatewayCommand;
        auto escrow = settings.find("sawtooth.escrow." + blockchain);
        if (escrow == settings.end())
        {
            throw sawtooth::InvalidTransaction("Escrow service is not configured for " + blockchain);
        }
        gatewayCommand << blockchain << " verify " << blockchainTxId << " " << escrow->second << " " << (amount + fee) << " " << sighash << " " << address.address() << " " << networkId;
        std::string response = "";
        bool rc = localGateway->send(gatewayCommand.str());
        if (rc)
        {
            localGateway->receive(response);
        }
        if (response.empty() || response == "miss") // couldn't interact with the local gateway or it wasn't able to validate
        {
            // need to reset the socket here or else it will always fail next time
            // TODO: need a way to handle the local gateway coming back online and start replying to old messages
            localGateway->close();
            delete localGateway;
            localGateway = new zmqpp::socket(*socketContext, zmqpp::socket_type::request);
            localGateway->connect(URL_GATEWAY);
            localGateway->set(zmqpp::socket_option::receive_timeout, LOCAL_SOCKET_TIMEOUT_MILLISECONDS);

            if (!externalGatewayAddress.empty())
            {
                externalGateway->connect(externalGatewayAddress);
                if (externalGateway->send(gatewayCommand.str()))
                {
                    externalGateway->receive(response);
                }
                externalGateway->disconnect(externalGatewayAddress);
            }
        }
        if (response != "good")
        {
            throw sawtooth::InvalidTransaction("Couldn't validate the transaction");
        }

        Transfer transfer;
        transfer.set_blockchain(blockchain);
        transfer.set_amount(amountString);
        transfer.set_fee(feeString);
        transfer.set_txid(blockchainTxId);
        transfer.set_network(networkId);
        transfer.set_sighash(sighash);

        std::vector<sawtooth::GlobalState::KeyValue> states;
        addState(&states, transferId, transfer);
        state->SetState(states);
    }

    void AddAskOrder(nlohmann::json const& query)
    {
        const std::string blockchain = getStringLower(query, "p1", "blockchain");
        boost::multiprecision::cpp_int amount;
        const std::string amountString = getBigint(query, "p2", "amount", &amount);
        const std::string interest = getBigint(query, "p3", "interest");
        const std::string collateralBlockchain = getStringLower(query, "p4", "collateralBlockchain");
        const std::string collateral = getBigint(query, "p5", "collateral");
        const std::string fee = getBigint(query, "p6", "fee");
        const unsigned long expiration = getDate(query, "p7", "expiration");
        const std::string transferId = getStringLower(query, "p8", "transferId");

        const std::string signer = txn->header()->GetValue(sawtooth::TransactionHeaderField::TransactionHeaderSignerPublicKey);
        const std::string askOrderId = namespacePrefix + ASK_ORDER + transferId.substr(NAMESPACE_PREFIX_LENGTH + PREFIX_LENGTH, std::string::npos);

        std::string stateData = getStateData(askOrderId);
        if (!stateData.empty())
        {
            throw sawtooth::InvalidTransaction("Duplicate id");
        }

        stateData = getStateData(transferId, true);
        Transfer transfer;
        transfer.ParseFromString(stateData);
        if (transfer.amount() != amountString)
        {
            throw sawtooth::InvalidTransaction("Invalid amount locked");
        }
        if (!transfer.orderid().empty())
        {
            throw sawtooth::InvalidTransaction("The transfer has been already used");
        }
        transfer.set_orderid(askOrderId);

        AskOrder askOrder;
        askOrder.set_blockchain(blockchain);
        askOrder.set_amount(amountString);
        askOrder.set_interest(interest);
        askOrder.set_collateral_blockchain(collateralBlockchain);
        askOrder.set_collateral(collateral);
        askOrder.set_fee(fee);
        askOrder.set_expiration(expiration);
        askOrder.set_transfer_id(transferId);
        askOrder.set_sighash(sha512id(signer));

        std::vector<sawtooth::GlobalState::KeyValue> states;
        addState(&states, askOrderId, askOrder);
        addState(&states, transferId, transfer);
        state->SetState(states);
    }

    void AddBidOrder(nlohmann::json const& query)
    {
        const std::string blockchain = getStringLower(query, "p1", "blockchain");
        const std::string amountString = getBigint(query, "p2", "amount");
        const std::string interest = getBigint(query, "p3", "interest");
        const std::string collateralBlockchain = getStringLower(query, "p4", "collateralBlockchain");
        const std::string collateral = getBigint(query, "p5", "collateral");
        const std::string fee = getBigint(query, "p6", "fee");
        const unsigned long expiration = getDate(query, "p7", "expiration");
        const std::string ordinal = getParam(query, "i", "ordinal");

        const std::string signer = txn->header()->GetValue(sawtooth::TransactionHeaderField::TransactionHeaderSignerPublicKey);
        const std::string id = makeAddress(BID_ORDER, ordinal + signer);
        std::string stateData = getStateData(id);
        if (!stateData.empty())
        {
            throw sawtooth::InvalidTransaction("Duplicate id");
        }

        BidOrder bidOrder;
        bidOrder.set_blockchain(blockchain);
        bidOrder.set_amount(amountString);
        bidOrder.set_interest(interest);
        bidOrder.set_collateral_blockchain(collateralBlockchain);
        bidOrder.set_collateral(collateral);
        bidOrder.set_fee(fee);
        bidOrder.set_expiration(expiration);
        bidOrder.set_sighash(sha512id(signer));

        std::vector<sawtooth::GlobalState::KeyValue> states;
        addState(&states, id, bidOrder);
        state->SetState(states);
    }

    void AddDealOrder(nlohmann::json const& query)
    {
        const std::string askOrderId = getStringLower(query, "p1", "askOrderId");
        const std::string bidOrderId = getStringLower(query, "p2", "bidOrderId");

        const std::string id = makeAddress(DEAL_ORDER, askOrderId + bidOrderId);
        std::string stateData = getStateData(id);
        if (!stateData.empty())
        {
            throw sawtooth::InvalidTransaction("Duplicate id");
        }

        const std::string signer = txn->header()->GetValue(sawtooth::TransactionHeaderField::TransactionHeaderSignerPublicKey);
        const std::string mySighash = sha512id(signer);

        stateData = getStateData(askOrderId, true);
        AskOrder askOrder;
        askOrder.ParseFromString(stateData);
        if (askOrder.sighash() != mySighash)
        {
            throw sawtooth::InvalidTransaction("Only an investor can add a deal order");
        }

        stateData = getStateData(bidOrderId, true);
        BidOrder bidOrder;
        bidOrder.ParseFromString(stateData);
        if (bidOrder.sighash() == mySighash)
        {
            throw sawtooth::InvalidTransaction("The ask and bid orders are from the same party");
        }

        DealOrder dealOrder;
        dealOrder.set_ask_order_id(askOrderId);
        dealOrder.set_bid_order_id(bidOrderId);

        std::vector<sawtooth::GlobalState::KeyValue> states;
        states.push_back(sawtooth::GlobalState::KeyValue(id, stateData));
        addState(&states, id, dealOrder);
        state->SetState(states);
    }

    void CompleteDealOrder(nlohmann::json const& query)
    {
        const std::string dealOrderId = getStringLower(query, "p1", "dealOrderId");
        const std::string collateralTransferId = getStringLower(query, "p2", "collateralTransferId");

        std::string stateData = getStateData(dealOrderId, true);
        DealOrder dealOrder;
        dealOrder.ParseFromString(stateData);
        if (!dealOrder.collateral_transfer_id().empty())
        {
            throw sawtooth::InvalidTransaction("The deal has been already completed");
        }

        stateData = getStateData(collateralTransferId, true);
        Transfer collateralTransfer;
        collateralTransfer.ParseFromString(stateData);
        if (!collateralTransfer.orderid().empty())
        {
            throw sawtooth::InvalidTransaction("The transfer has been already used");
        }

        const std::string signer = txn->header()->GetValue(sawtooth::TransactionHeaderField::TransactionHeaderSignerPublicKey);
        const std::string mySighash = sha512id(signer);

        stateData = getStateData(dealOrder.bid_order_id(), true);
        BidOrder bidOrder;
        bidOrder.ParseFromString(stateData);
        if (bidOrder.sighash() != mySighash)
        {
            throw sawtooth::InvalidTransaction("Only a fundraiser can complete a deal");
        }
        boost::multiprecision::cpp_int fee = getBigint(bidOrder.fee());

        if (bidOrder.collateral_blockchain() != collateralTransfer.blockchain() || bidOrder.collateral() != collateralTransfer.amount())
        {
            throw sawtooth::InvalidTransaction("The transfer doesn't match the bid order");
        }

        stateData = getStateData(dealOrder.ask_order_id(), true);
        AskOrder askOrder;
        askOrder.ParseFromString(stateData);

        const std::string srcWalletId = namespacePrefix + WALLET + mySighash;
        stateData = getStateData(srcWalletId, true);
        if (stateData.empty())
        {
            throw sawtooth::InvalidTransaction("Insufficient funds");
        }

        Wallet srcWallet;
        srcWallet.ParseFromString(stateData);
        boost::multiprecision::cpp_int srcBalance = getBigint(srcWallet.amount());
        if (srcBalance - fee < 0)
        {
            throw sawtooth::InvalidTransaction("Insufficient funds");
        }

        srcBalance -= fee;
        srcWallet.set_amount(toString(srcBalance));

        const std::string dstWalletId = namespacePrefix + WALLET + askOrder.sighash();
        stateData = getStateData(dstWalletId);

        Wallet dstWallet;
        if (stateData.empty())
        {
            dstWallet.set_amount(bidOrder.fee());
        }
        else
        {
            dstWallet.ParseFromString(stateData);
            boost::multiprecision::cpp_int dstBalance = getBigint(dstWallet.amount());
            dstBalance += fee;
            dstWallet.set_amount(toString(dstBalance));
        }

        dealOrder.set_collateral_transfer_id(collateralTransferId);

        collateralTransfer.set_orderid(dealOrderId);

        std::vector<sawtooth::GlobalState::KeyValue> states;
        addState(&states, dealOrderId, dealOrder);
        addState(&states, collateralTransferId, collateralTransfer);
        addState(&states, srcWalletId, srcWallet);
        addState(&states, dstWalletId, dstWallet);
        state->SetState(states);
    }

    void AddRepaymentOrder(nlohmann::json const& query)
    {
        const std::string dealOrderId = getStringLower(query, "p1", "dealOrderId");

        const std::string id = namespacePrefix + REPAYMENT_ORDER + dealOrderId.substr(NAMESPACE_PREFIX_LENGTH + PREFIX_LENGTH, std::string::npos);
        std::string stateData = getStateData(id);
        if (!stateData.empty())
        {
            throw sawtooth::InvalidTransaction("A repayment order already exist for the deal order");
        }

        RepaymentOrder repaymentOrder;
        repaymentOrder.set_deal_id(dealOrderId);

        std::vector<sawtooth::GlobalState::KeyValue> states;
        addState(&states, id, repaymentOrder);
        state->SetState(states);
    }

    void CompleteRepaymentOrder(nlohmann::json const& query)
    {
        const std::string repaymentOrderId = getStringLower(query, "p1", "repaymentOrderId");
        const std::string transferId = getStringLower(query, "p2", "transferId");

        std::string stateData = getStateData(repaymentOrderId, true);
        RepaymentOrder repaymentOrder;
        repaymentOrder.ParseFromString(stateData);

        stateData = getStateData(repaymentOrder.deal_id(), true);
        DealOrder dealOrder;
        dealOrder.ParseFromString(stateData);

        stateData = getStateData(dealOrder.ask_order_id(), true);
        AskOrder askOrder;
        askOrder.ParseFromString(stateData);

        stateData = getStateData(transferId, true);
        Transfer transfer;
        transfer.ParseFromString(stateData);
        if (!transfer.orderid().empty())
        {
            throw sawtooth::InvalidTransaction("The transfer has been already used");
        }
        transfer.set_orderid(repaymentOrderId);

        if (transfer.blockchain() != askOrder.blockchain() || transfer.amount() < askOrder.amount())
        {
            throw sawtooth::InvalidTransaction("The transfer doesn't match the ask order");
        }

        repaymentOrder.set_transfer_id(transferId);

        std::vector<sawtooth::GlobalState::KeyValue> states;
        addState(&states, repaymentOrderId, repaymentOrder);
        addState(&states, transferId, transfer);
        state->SetState(states);
    }

    void UnlockFunds(nlohmann::json const& query)
    {
        const std::string dealOrderId = getStringLower(query, "p1", "dealOrderId");
        const std::string addressId = getStringLower(query, "p2", "addressId");

        verifyGatewaySigner();

        std::string stateData = getStateData(dealOrderId, true);
        DealOrder dealOrder;
        dealOrder.ParseFromString(stateData);
        if (!dealOrder.unlock_funds_destination_address_id().empty())
        {
            throw sawtooth::InvalidTransaction("The funds have been already unlocked for the deal");
        }

        dealOrder.set_unlock_funds_destination_address_id(addressId);

        std::vector<sawtooth::GlobalState::KeyValue> states;
        addState(&states, dealOrderId, dealOrder);
        state->SetState(states);
    }

    void UnlockCollateral(nlohmann::json const& query)
    {
        const std::string repaymentOrderId = getStringLower(query, "p1", "repaymentOrderId");
        const std::string addressId = getStringLower(query, "p2", "addressId");

        verifyGatewaySigner();

        std::string stateData = getStateData(repaymentOrderId, true);
        RepaymentOrder repaymentOrder;
        repaymentOrder.ParseFromString(stateData);
        stateData = getStateData(repaymentOrder.deal_id(), true);
        DealOrder dealOrder;
        dealOrder.ParseFromString(stateData);
        if (!dealOrder.unlock_collateral_destination_address_id().empty())
        {
            throw sawtooth::InvalidTransaction("The collateral has been already unlocked for the deal");
        }

        dealOrder.set_unlock_collateral_destination_address_id(addressId);

        std::vector<sawtooth::GlobalState::KeyValue> states;
        addState(&states, repaymentOrder.deal_id(), dealOrder);
        state->SetState(states);
    }

    void CollectCoins(nlohmann::json const& query)
    {
        const std::string transferId = getStringLower(query, "p1", "transferId");
        std::string stateData = getStateData(transferId, true);
        Transfer transfer;
        transfer.ParseFromString(stateData);
        if (transfer.blockchain() != "ethereum" || transfer.network() != "creditcoin")
        {
            throw sawtooth::InvalidTransaction("Invalid blockchain");
        }
        if (!transfer.orderid().empty())
        {
            throw sawtooth::InvalidTransaction("The coins have been already collected");
        }

        const std::string signer = txn->header()->GetValue(sawtooth::TransactionHeaderField::TransactionHeaderSignerPublicKey);
        const std::string mySighash = sha512id(signer);

        if (transfer.sighash() != mySighash)
        {
            throw sawtooth::InvalidTransaction("Only the owner can collect");
        }

        transfer.set_orderid("$");

        const std::string walletId = namespacePrefix + WALLET + mySighash;
        stateData = getStateData(walletId);

        Wallet wallet;
        if (stateData.empty())
        {
            wallet.set_amount(transfer.amount());
        }
        else
        {
            wallet.ParseFromString(stateData);
            boost::multiprecision::cpp_int balance = getBigint(wallet.amount());
            balance += getBigint(transfer.amount());
            wallet.set_amount(toString(balance));
        }

        std::vector<sawtooth::GlobalState::KeyValue> states;
        addState(&states, walletId, wallet);
        addState(&states, transferId, transfer);
        state->SetState(states);
    }
};

class Handler : public sawtooth::TransactionHandler
{
public:
    Handler()
    {
    }

    std::string transaction_family_name() const
    {
        return NAMESPACE;
    }

    std::list<std::string> versions() const
    {
        return { "1.0" };
    }

    std::list<std::string> namespaces() const
    {
        return { namespacePrefix };
    }

    sawtooth::TransactionApplicatorUPtr GetApplicator(sawtooth::TransactionUPtr txn, sawtooth::GlobalStateUPtr state)
    {
        return sawtooth::TransactionApplicatorUPtr(new Applicator(std::move(txn), std::move(state)));
    }
};

static void setupSettingsAndExternalGatewayAddress()
{
    try
    {
        filter(SETTINGS_NAMESPACE, [](std::string const& address, std::vector<std::uint8_t> const& protobuf) {
            Setting setting;
            setting.ParseFromArray(protobuf.data(), static_cast<int>(protobuf.size()));
            for (auto entry : setting.entries())
            {
                settings[entry.key()] = entry.value();
            }
        });

        auto i = settings.find("sawtooth.validator.gateway");
        if (i != settings.end())
        {
            std::string address = i->second;
            if (address.find("tcp://") != 0)
            {
                address = "tcp://" + address;
            }
            externalGatewayAddress = address;
        }
    }
    catch (...)
    {
    }
    if (externalGatewayAddress.empty())
    {
        std::cerr << "sawtooth.validator.gateway not found" << std::endl;
    }
}

int main(int argc, char** argv)
{
    // --------------------------- optional steps (rpc server is not used in this implementation
    // run bitcoind.exe (from C:\Program Files\Bitcoin\daemon)
    //    bitcoind.exe -testnet -datadir=D:\Job\gluwa\bitcoin\testnetdata -txindex
    //        the folder contains folder testnet3 where files .lock and wallets\.walletlock exisr, they need to be deleted if errors are reported
    //            then: bitcoin-cli.exe -testnet -datadir=D:\Job\gluwa\bitcoin\testnetdata <<command>>
    // -------------------------------------------------------------------------------------------------------
    // make sure Docker for Windows is running
    // console1> docker-compose.exe -f .\sawtooth-default.yaml up
    // --------------------------- optional steps in debugiing environment (mandatory in deployed environment)
    // console2> docker exec -it sawtooth-validator-default bash
    // console2> [bash]:/# sawset proposal create --url http://rest-api:8008 --key /root/.sawtooth/keys/my_key.priv sawtooth.validator.gateway="10.10.10.10:55555"
    // console2> [bash]:/# sawset proposal create --url http://rest-api:8008 --key /root/.sawtooth/keys/my_key.priv sawtooth.escrow.bitcoin="...address..."
    // console2> [bash]:/# sawset proposal create --url http://rest-api:8008 --key /root/.sawtooth/keys/my_key.priv sawtooth.escrow.ethereum="...address..."
    // console2> [bash]:/# sawset proposal create --url http://rest-api:8008 --key /root/.sawtooth/keys/my_key.priv sawtooth.gateway.sighash="...sighash..."
    // console2> [bash]:/# sawset proposal create --url http://rest-api:8008 --key /root/.sawtooth/keys/my_key.priv sawtooth.validator.transaction_families='[{"family": "creditcoin", "version": "1.0"}, {"family":"sawtooth_settings", "version":"1.0"}]'
    // xonsole2> [bash]:/# sawtooth settings list --url http://rest-api:8008
    // open http://localhost:8008 in a browser
    // -------------------------------------------------------------------------------------------------------
    // console3> ccgateway
    // console4> ccprocessor
    // console5> ccclient creditcoin tmpAddDeal bitcoin mvJr4KdZdx7NzJL87Xx5FNstP1tttbGvq2 10500 bitcoin mp3PRSq1ZKtSDxTqSwWSBLCm3EauHcVD7g 10000
    // console5> ccclient bitcoin registerTransfer 8a1a04aa595e25f63564472cebc9337366bcd4495aee0c50130e0b32975d78313ff35b
	parseArgs(argc, argv);
    setupSettingsAndExternalGatewayAddress();

    try
    {
        zmqpp::context context;
        socketContext = &context;

        localGateway = new zmqpp::socket(context, zmqpp::socket_type::request);
        localGateway->connect(URL_GATEWAY);
        localGateway->set(zmqpp::socket_option::receive_timeout, LOCAL_SOCKET_TIMEOUT_MILLISECONDS);

        externalGateway = new zmqpp::socket(context, zmqpp::socket_type::request);
        externalGateway->set(zmqpp::socket_option::receive_timeout, SOCKET_TIMEOUT_MILLISECONDS);

        log4cxx::BasicConfigurator::configure();
        
        sawtooth::TransactionProcessorUPtr processor(sawtooth::TransactionProcessor::Create(URL_VALIDATOR));
        sawtooth::TransactionHandlerUPtr transactionHandler(new Handler());
        processor->RegisterHandler(std::move(transactionHandler));

        std::cout << "Running" << std::endl;
        processor->Run();

        localGateway->close();
        delete localGateway;

        externalGateway->close();
        delete externalGateway;

        return 0;
    }
    catch (std::exception& e)
    {
        std::cerr << "Unexpected exception: " << e.what() << std::endl;
    }
    catch (...)
    {
        std::cerr << "Unexpected exception" << std::endl;
    }

    localGateway->close();
    delete localGateway;

    externalGateway->close();
    delete externalGateway;

    return -1;
}