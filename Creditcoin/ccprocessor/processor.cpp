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
#include <sstream>
#include <iomanip>
#include <mutex>
#include <atomic>
#include <fstream>
#include <thread>
#include <chrono>
using namespace std::chrono_literals;

#include <cryptopp/sha.h>
#include <cryptopp/filters.h>
#include <cryptopp/hex.h>
#include <cryptopp/hrtimer.h>

#include <exceptions.h>
#include <setting.pb.h>

#include <log4cxx/basicconfigurator.h>
#include <log4cxx/logger.h>
#include <nlohmann/json/json.hpp>
#include <zmqpp/context.hpp>
#include <zmqpp/socket.hpp>
#include <zmqpp/socket_types.hpp>

#include <boost/algorithm/string.hpp>
#include <boost/multiprecision/cpp_int.hpp>

#include "Address.pb.h"
#include "AskOrder.pb.h"
#include "BidOrder.pb.h"
#include "DealOrder.pb.h"
#include "Offer.pb.h"
#include "RepaymentOrder.pb.h"
#include "Transfer.pb.h"
#include "Wallet.pb.h"
#include "Fee.pb.h"

#include <sawtooth_sdk.h>

const int URL_PREFIX_LEN = 6;
const size_t MERKLE_ADDRESS_LENGTH = 70;
const size_t NAMESPACE_PREFIX_LENGTH = 6;
const size_t PREFIX_LENGTH = 4;
const int SKIP_TO_GET_60 = 512 / 8 * 2 - 60; // 512 - hash size in bits, 8 - bits in byte, 2 - hex digits for byte, 60 - merkle address length (70) without namespace length (6) and prexix length (4)

const char* URL_PREFIX = "tcp://";
static std::string URL_GATEWAY = "tcp://localhost:55555";
static std::string URL_VALIDATOR = "tcp://localhost:4004";
static const std::string TRANSFERS_ROOT = "TRANSFERS_ROOT";
static const std::string NAMESPACE = "CREDITCOIN";
static const std::string SETTINGS_NAMESPACE = "000000";

static const char WALLET[] = "0000";
//static_assert(sizeof(WALLET) / sizeof(char) - 1 == PREFIX_LENGTH);
static const char ADDR[] = "1000";
//static_assert(sizeof(ADDR) / sizeof(char) - 1 == PREFIX_LENGTH);
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
static const char OFFER[] = "7000";
//static_assert(sizeof(OFFER) / sizeof(char) - 1 == PREFIX_LENGTH);
static const char ERC20[] = "8000";
//static_assert(sizeof(ERC20) / sizeof(char) - 1 == PREFIX_LENGTH);
static const char PROCESSED_BLOCK[] = "9000";
//static_assert(sizeof(PROCESSED_BLOCK) / sizeof(char) - 1 == PREFIX_LENGTH);
static const char FEE[] = "0100";
//static_assert(sizeof(FEE) / sizeof(char) - 1 == PREFIX_LENGTH);

static const char* PROCESSED_BLOCK_ID = "000000000000000000000000000000000000000000000000000000000000";

static char const* RPC_FAILURE = "Failed to process RPC response";
static char const* DATA = "data";
static char const* ADDRESS = "address";
static char const* ERR = "error";
static char const* BATCHES = "batches";
static char const* TRANSACTIONS = "transactions";
static char const* NONCE = "nonce";
static char const* HEADER = "header";
static char const* BLOCK_NUM = "block_num";
static char const* PAGING = "paging";
static char const* NEXT = "next";
static char const* SIGNER_PUBLIC_KEY = "signer_public_key";

static const int INTEREST_MULTIPLIER = 1000000;

static const int SOCKET_TIMEOUT_MILLISECONDS = 5000000; // TODO make configurable or set a more reasonable time for prod env
// TODO re-evaluate the need for this after gateway changes
#if !defined(NDEBUG)
static const int LOCAL_SOCKET_TIMEOUT_MILLISECONDS = 5000000;
#else
static const int LOCAL_SOCKET_TIMEOUT_MILLISECONDS = 5000;
#endif

static const int CONFIRMATION_COUNT = 30;
static const int YEAR_OF_BLOCKS = 60 * 24 * 365;
static const int BLOCKS_IN_PERIOD = YEAR_OF_BLOCKS * 6;
static const boost::multiprecision::cpp_int BLOCKS_IN_PERIOD_UPDATE1 = 2500000;
static const int REMAINDER_OF_LAST_PERIOD = 2646631;
static const int BLOCK_REWARD_PROCESSING_COUNT = 10;

static char const* TX_FEE_STRING = "10000000000000000";
static boost::multiprecision::cpp_int TX_FEE(TX_FEE_STRING);

static char const* REWARD_AMOUNT_STRING = "222000000000000000000";
static const boost::multiprecision::cpp_int REWARD_AMOUNT(REWARD_AMOUNT_STRING);

static std::atomic<std::map<std::string, std::string>*> settings(new std::map<std::string, std::string>());
static std::atomic<std::string*> externalGatewayAddress(new std::string());
static zmqpp::socket* localGateway;
static std::mutex localGatewayLock;
static zmqpp::socket* externalGateway;
static zmqpp::context* socketContext;

static std::mutex killerLock;
static bool killerStarted = false;
static std::chrono::system_clock::time_point lastTime;

static log4cxx::LoggerPtr logger(log4cxx::Logger::getLogger("sawtooth.TransactionProcessor"));

static bool transitioning;

struct Tx
{
    std::string sighash;
    std::string guid;
    std::vector<std::uint8_t> payload;
};

struct SimpleBlock
{
    std::string signer;
    std::vector<Tx> txs;
};

struct Ctx
{
    std::string sighash;
    std::string guid;
    int tip;
    bool replaying;
    bool transitioning;
    std::map<std::string, std::string> currentState;

    Ctx(): tip(0), replaying(false), transitioning(::transitioning)
    {
    }
};

struct TxPos
{
    int block;
    int txIdx;

    TxPos(): block(0), txIdx(0)
    {
    }

    TxPos(int b, int i): block(b), txIdx(i)
    {
    }
};

static std::map<std::string, TxPos> txguid2prevblockidx;
static std::vector<SimpleBlock> blocks;

static std::mutex stateUpdateLock;
static std::map<std::string, std::string> transitioningState;
static std::map<std::string, std::string> tipCurrentState;
static int updatedBlockIdx;
static int updatedTxIdx;

static std::unique_ptr<std::thread> updatingSettings;

static boost::multiprecision::cpp_int v2block;

sawtooth::GlobalStateUPtr contextlessState;

static int dealExpFixBlock = 278890;

#if IS_LINUX
char const* const transitionFile = "/home/Creditcoin/cctt/data/transition.txt";
#else
char const* const transitionFile = "C:\\transition.txt";
#endif

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
    int shift = 0;
    if (argc >= 2)
    {
        static char dealExpFixBlockPrefix[] = "-dealExpFixBlock:";
        std::string dealExpFixBlockStr(argv[1]);
        if (dealExpFixBlockStr.rfind(dealExpFixBlockPrefix, 0) == 0)
        {
            dealExpFixBlockStr = dealExpFixBlockStr.substr((sizeof(dealExpFixBlockPrefix) - sizeof(char)) / sizeof(char));
            dealExpFixBlock = std::stoi(dealExpFixBlockStr);
            shift++;
            argc--;
        }
    }

    if (argc >= 2)
    {
        /* tmp hax, remove url test to support docker endpoints
        if (!testConnectString(argv[1]))
        {
            std::cerr << "Connect string is not in format host:port - " << argv[1] << std::endl;
            usage();
        }
        */
        URL_VALIDATOR = argv[shift + 1];
    }
    std::cout << "Connecting to " << URL_VALIDATOR << std::endl;

    if (argc >= 3)
    {
        URL_GATEWAY = argv[shift + 2];
    }
    std::cout << "Using gateway URL: " << URL_GATEWAY << std::endl;
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

static std::vector<std::uint8_t> toVector(std::string const& in)
{
    std::vector<std::uint8_t> out(in.begin(), in.end());
    return out;
}

// filter should paginate
static void filter(Ctx const& ctx, std::string const& prefix, std::function<void(std::string const&, std::string const&)> const& lister)
{
    //no pagination needed!?
    if (ctx.transitioning)
    {
        auto currentState = ctx.currentState;
        currentState.insert(tipCurrentState.begin(), tipCurrentState.end());
        for (auto& e: transitioningState)
        {
            auto updated = currentState.find(e.first);
            if (updated == currentState.end())
            {
                if (e.first.rfind(prefix, 0) == 0 && e.second.size() > 0)
                    lister(e.first, e.second);
            }
        }
        for (auto& e: currentState)
        {
            if (e.first.rfind(prefix, 0) == 0 && e.second.size() > 0)
                lister(e.first, e.second);
        }

        return;
    }


    std::string root;
    std::string start;
    try
    {
        do
        {
            std::vector<sawtooth::GlobalState::KeyValue> states_paginated_slice;
            contextlessState->GetStatesByPrefix(prefix, &root, &start, &states_paginated_slice);

            // don't rely on vector length, the vector holds a paginated subset only.
            for (auto& state : states_paginated_slice)
            {
                std::string address = state.first;
                std::string protobuf = state.second;
                lister(address, protobuf);
            }

        } while (!start.empty());
    }
    catch (sawtooth::InvalidTransaction const&)
    {
        throw;
    }
    catch (std::exception const& e)
    {
        //make it throw on purpose to test logs
        LOG4CXX_DEBUG(logger, "filter op failed");
        throw sawtooth::InvalidTransaction(e.what());
    }
}

static boost::multiprecision::cpp_int getBigint(std::string const& bigint, bool allowNegative = false)
{
    boost::multiprecision::cpp_int ret;
    try
    {
        ret = boost::multiprecision::cpp_int(bigint);
    }
    catch (std::runtime_error const&)
    {
        throw sawtooth::InvalidTransaction("Invalid number format");
    }
    if (!allowNegative && ret < 0)
    {
        throw sawtooth::InvalidTransaction("Expecting a positive value");
    }
    return ret;
}

static std::string trimQuotes(std::string const& quotedStr)
{
    auto quotedStrLen = quotedStr.length();
    assert(quotedStr[0] == '"' && quotedStr[quotedStrLen - 1] == '"');
    return quotedStr.substr(1, quotedStrLen - 2);
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

static void doUpdateSettings()
{
    try
    {
        assert(!transitioning);

        std::unique_ptr<std::map<std::string, std::string>> newSettings(new std::map<std::string, std::string>());
        filter(Ctx(), SETTINGS_NAMESPACE, [&newSettings](std::string const& address, std::string const& protobuf) {
            Setting setting;
            setting.ParseFromString(protobuf);
            for (auto& entry : setting.entries())
            {
                (*newSettings)[entry.key()] = entry.value();
            }
        });
        {
            //nothrow segment:
            auto updatedSettings = newSettings.release();
            auto oldSettings = settings.exchange(updatedSettings);
            delete oldSettings; //dtor of the map shouldn't throw as it only uses std::strings
        }

        auto actualSettings = settings.load();
        auto i = actualSettings->find("sawtooth.validator.gateway");
        if (i != actualSettings->end())
        {
            std::unique_ptr<std::string> address(new std::string(i->second));
            if (address->find("tcp://") != 0)
            {
                *address = "tcp://" + *address;
            }
            delete externalGatewayAddress.exchange(address.release());
        }
        i = actualSettings->find("creditcoin.v2block");
        if (i != actualSettings->end())
        {
            v2block = getBigint(i->second);
        }
    }
    catch (...)
    {
    }
}

static void cleanupTransitioning()
{
    std::this_thread::sleep_for(60s);
    std::remove(transitionFile);
    exit(0);
}

static void killer()
{
    for (;;)
    {
        std::this_thread::sleep_for(60s);
        auto now = std::chrono::system_clock::now();
        bool idle;
        {
            std::lock_guard<std::mutex> guard(killerLock);
            idle = lastTime + 300s < now;
        }
        if (idle)
            cleanupTransitioning();
    }
}

static void updateSettings()
{
    // this is a thread routine, we don't join the thread and don't care if it's in progress when the process termitates, just terminating it is ok
    for (;;)
    {
        std::this_thread::sleep_for(6s);
        doUpdateSettings();
        std::this_thread::sleep_for(6000s);
    }
}

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

static std::string toString(std::vector<std::uint8_t> const& v)
{
    const char* data = reinterpret_cast<const char*>(v.data());
    std::string out(data, data + v.size());
    return out;
}

static ::google::protobuf::uint64 parseUint64(std::string const& numberString) {
    ::google::protobuf::uint64 number;
    std::istringstream iss(numberString);
    iss >> number;
    if (iss.fail() || !iss.eof())
    {
        throw sawtooth::InvalidTransaction("Invalid number");
    }
    return number;
}

static std::string getStringLower(nlohmann::json const& query, std::string const& id, std::string const& name)
{
    std::string ret = getString(query, id, name);
    boost::to_lower(ret);
    return ret;
}

static ::google::protobuf::uint64 getUint64(nlohmann::json const& query, std::string const& id, std::string const& name)
{
    return parseUint64(getString(query, id, name));
}

static std::string toString(boost::multiprecision::cpp_int const& bigint)
{
    std::stringstream ss;
    ss << bigint;
    return ss.str();
}

static std::string compress(std::string const& uncompressed)
{
    // uncompressed key is 0x04 + x + y, where x and y are 32 bytes each
    // to compress we use 0x02 + x if y is even or 0x03 + x if y is odd

    std::string ret;

    std::string marker = uncompressed.substr(0, 2);
    if (uncompressed.length() == 2 * (1 + 2 * 32) && isHex(uncompressed) && marker == "04")
    {
        std::string x = uncompressed.substr(2 * 1, 2 * 32);
        std::string yLast = uncompressed.substr(2 * (1 + 32 + 31), 2 * 1);

        std::istringstream iss(yLast);
        unsigned int last;
        iss >> std::hex >> last;
        if (!iss.fail() && iss.eof())
        {
            if (last % 2)
            {
                ret = "03" + x;
            }
            else
            {
                ret = "02" + x;
            }
        }
    }
    else if ((marker == "02" || marker == "03") && uncompressed.length() == 66)
    {
        return uncompressed;
    }

    if (!ret.length())
    {
        throw sawtooth::InvalidTransaction("Unexpected public key format");
    }

    return ret;
}

static std::string getFromHeader(nlohmann::json const& block, char const* fieldName)
{
    if (block.count(HEADER) != 1)
    {
        throw sawtooth::InvalidTransaction(RPC_FAILURE);
    }
    auto& header = block[HEADER];
    if (!header.is_object())
    {
        throw sawtooth::InvalidTransaction(RPC_FAILURE);
    }
    return header[fieldName];
}

static std::string blockNum(nlohmann::json const& block)
{
    std::string num = getFromHeader(block, BLOCK_NUM);
    return num;
}

static boost::multiprecision::cpp_int blockNumInt(nlohmann::json const& block)
{
    std::string num = blockNum(block);
    return getBigint(num);
}

boost::multiprecision::cpp_int calcInterest(boost::multiprecision::cpp_int const& amount, boost::multiprecision::cpp_int const& ticks, boost::multiprecision::cpp_int const& interest)
{
    boost::multiprecision::cpp_int total = amount;
    for (boost::multiprecision::cpp_int i = 0; i < ticks; ++i)
    {
        boost::multiprecision::cpp_int compound = (total * interest) / INTEREST_MULTIPLIER;
        total += compound;
    }
    return total;
}

class Applicator : public sawtooth::TransactionApplicator
{
public:
    Applicator(sawtooth::TransactionUPtr txn, sawtooth::GlobalStateUPtr state) :
        TransactionApplicator(std::move(txn), std::move(state))
    {
    };

    void Apply(std::string const& cmd, nlohmann::json const& query)
    {
        if (v2block != 0 && lastBlockInt(ctx) > v2block)
        {
            std::string const& version = txn->header()->GetValue(sawtooth::TransactionHeaderField::TransactionHeaderFamilyVersion);
            if (version[0] == '1')
                throw sawtooth::InvalidTransaction("Version 1 transactions are not accepted after 2.0");
        }
#ifndef NDEBUG
        {
            //std::ofstream log("B:\\Job\\gluwa\\prod\\transition.log", std::ios::app);
            //std::stringstream l;
            //l << ctx.tip << ": " << ctx.guid << " - " << cmd << "\n";
            //for (int i = 1; ; ++i)
            //{
            //    std::stringstream p;
            //    p << "p" << i;
            //    auto pp = p.str();
            //    auto param = query.find(pp);
            //    if (param == query.end())
            //        break;
            //    l << " " << param->dump();
            //}
            //auto ll = l.str();
            //log << ll;
            //OutputDebugStringA((ll.c_str());
        }
#endif
        if (boost::iequals(cmd, "SendFunds"))
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
        else if (boost::iequals(cmd, "AddOffer"))
        {
            AddOffer(query);
        }
        else if (boost::iequals(cmd, "AddDealOrder"))
        {
            AddDealOrder(query);
        }
        else if (boost::iequals(cmd, "CompleteDealOrder"))
        {
            CompleteDealOrder(query);
        }
        else if (boost::iequals(cmd, "LockDealOrder"))
        {
            LockDealOrder(query);
        }
        else if (boost::iequals(cmd, "CloseDealOrder"))
        {
            CloseDealOrder(query);
        }
        else if (boost::iequals(cmd, "Exempt"))
        {
            Exempt(query);
        }
        else if (boost::iequals(cmd, "AddRepaymentOrder"))
        {
            AddRepaymentOrder(query);
        }
        else if (boost::iequals(cmd, "CompleteRepaymentOrder"))
        {
            CompleteRepaymentOrder(query);
        }
        else if (boost::iequals(cmd, "CloseRepaymentOrder"))
        {
            CloseRepaymentOrder(query);
        }
        else if (boost::iequals(cmd, "CollectCoins"))
        {
            CollectCoins(query);
        }
        else if (boost::iequals(cmd, "Housekeeping"))
        {
            Housekeeping(query);
        }
        else
        {
            std::stringstream error;
            error << "invalid command: '" << cmd << "'";
            throw sawtooth::InvalidTransaction(error.str());
        }
    }

    void doApply(std::string const& cmd, nlohmann::json const& query, std::string const& guid, std::string const& sighash)
    {
        ctx.guid = guid;
        ctx.sighash = sighash;

        Apply(cmd, query);

        ctx.currentState.insert(tipCurrentState.begin(), tipCurrentState.end());
        tipCurrentState.swap(ctx.currentState);
        ctx.currentState.clear();
    }

    void execute(std::vector<std::uint8_t>& payload, std::string const& guid, std::string const& sighash)
    {
        if (payload.size() > 0 && guid.size() > 0)
        {
            std::string cmd;
            auto query = cborToParams(payload, &cmd);
            ctx.replaying = true;
            doApply(cmd, query, guid, sighash);
            ctx.replaying = false;
        }
    }

    void Apply(std::string const& cmd, nlohmann::json const& query, std::string const& guid, std::string const& sighash)
    {
        if (ctx.transitioning)
        {
            auto found = txguid2prevblockidx.find(guid);
            if (found == txguid2prevblockidx.end())
            {
                cleanupTransitioning();
            }
            else
            {
                int tip = found->second.block;
                int currentBlockIdx = tip + 1;
                int txIdx = found->second.txIdx;

                std::lock_guard<std::mutex> guard(stateUpdateLock);

                if (currentBlockIdx > updatedBlockIdx)
                {
                    ctx.tip = updatedBlockIdx - 1;
                    for (int i = updatedTxIdx + 1; i < blocks[updatedBlockIdx].txs.size(); ++i)
                    {
                        auto& tx = blocks[updatedBlockIdx].txs[i];
                        if (tx.sighash.size() > 0)
                            execute(tx.payload, tx.guid, tx.sighash);
                    }
                    for (auto& e : tipCurrentState)
                        transitioningState[e.first] = e.second;
                    tipCurrentState.clear();
                    for (int i = updatedBlockIdx + 1; i < currentBlockIdx; ++i)
                    {
                        ctx.tip = i - 1;
                        for (auto& tx : blocks[i].txs)
                            if (tx.sighash.size() > 0)
                                execute(tx.payload, tx.guid, tx.sighash);

                        for (auto& e : tipCurrentState)
                            transitioningState[e.first] = e.second;
                        tipCurrentState.clear();
                    }

                    ctx.tip = tip;
                    for (int i = 0; i < txIdx; ++i)
                    {
                        auto& tx = blocks[updatedBlockIdx].txs[i];
                        if (tx.sighash.size() > 0)
                            execute(tx.payload, tx.guid, tx.sighash);
                    }
                }
                else if (currentBlockIdx == updatedBlockIdx)
                {
                    ctx.tip = tip;
                    if (txIdx <= updatedTxIdx)
                    {
                        tipCurrentState.clear();
                        for (int i = 0; i < txIdx; ++i)
                        {
                            auto& tx = blocks[updatedBlockIdx].txs[i];
                            if (tx.sighash.size() > 0)
                                execute(tx.payload, tx.guid, tx.sighash);
                        }
                    }
                    else
                    {
                        for (int i = updatedTxIdx + 1; i < txIdx; ++i)
                        {
                            auto& tx = blocks[updatedBlockIdx].txs[i];
                            if (tx.sighash.size() > 0)
                                execute(tx.payload, tx.guid, tx.sighash);
                        }
                    }
                }

                doApply(cmd, query, guid, sighash);

                updatedBlockIdx = currentBlockIdx;
                updatedTxIdx = txIdx;

                if (updatedBlockIdx == blocks.size() - 1 && updatedTxIdx == blocks[updatedBlockIdx].txs.size() - 1)
                {
                    std::cout << "Revalidated last block, terminating in a minute" << std::endl;
                    std::thread(cleanupTransitioning).detach();
                }
            }
            std::lock_guard<std::mutex> guard(killerLock);
            lastTime = std::chrono::system_clock::now();
            if (!killerStarted)
            {
                std::thread(killer).detach();
                killerStarted = true;
            }
        }
        else
        {
            Apply(cmd, query);
        }
    }

    void Apply()
    {
        std::cout << "Applicator::Apply" << std::endl;

        std::string cmd;
        auto query = cborToParams(&cmd);
        auto nounce = txn->header()->GetValue(sawtooth::TransactionHeaderField::TransactionHeaderNonce);
        Apply(cmd, query, nounce, sha512id(compress(txn->header()->GetValue(sawtooth::TransactionHeaderField::TransactionHeaderSignerPublicKey))));
    }

    static nlohmann::json cborToParams(std::vector<uint8_t> const& payload, std::string* cmd)
    {
        nlohmann::json query = nlohmann::json::from_cbor(payload);

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
    }

    nlohmann::json cborToParams(std::string* cmd)
    {
        const std::string& rawData = txn->payload();
        std::vector<uint8_t> dataVector = toVector(rawData);

        return cborToParams(dataVector, cmd);
    };

    boost::multiprecision::cpp_int lastBlockInt(Ctx const& ctx)
    {
        if (ctx.transitioning)
            return ctx.tip;
        else
            return boost::multiprecision::cpp_int(state->GetTip() - 1);
    }

    std::string lastBlock(Ctx const& ctx)
    {
        return toString(lastBlockInt(ctx));
    }

    bool getState(sawtooth::GlobalState* state, std::string* stateData, std::string const& id)
    {
        if (ctx.transitioning)
        {
            auto s = ctx.currentState.find(id);
            if (s != ctx.currentState.end())
            {
                *stateData = s->second;
                return true;
            }
            s = tipCurrentState.find(id);
            if (s != tipCurrentState.end())
            {
                *stateData = s->second;
                return true;
            }
            s = transitioningState.find(id);
            if (s != transitioningState.end())
            {
                *stateData = s->second;
                return true;
            }
            return true;
        }
        else
        {
            return state->GetState(stateData, id);
        }
    }

    void setState(sawtooth::GlobalState* state, std::vector<sawtooth::GlobalState::KeyValue> const& states)
    {
        if (ctx.transitioning)
        {
            for (auto i : states)
            {
                ctx.currentState[i.first] = i.second;
#ifndef NDEBUG
                //std::stringstream s;
                //s << i.first << ": " << i.second << "\n";
                //OutputDebugStringA(s.str().c_str());
#endif
            }
        }
        if (!ctx.replaying)
            state->SetState(states);
    }

    void setState(sawtooth::GlobalState* state, std::string const& stateData, std::string const& id)
    {
        if (ctx.transitioning)
        {
            ctx.currentState[id] = stateData;
#ifndef NDEBUG
            //std::stringstream s;
            //s << id << ": " << stateData << "\n";
            //OutputDebugStringA(s.str().c_str());
#endif
        }
        if (!ctx.replaying)
            state->SetState(id, stateData);
    }

    void deleteState(sawtooth::GlobalState* state, std::string const& id)
    {
        if (ctx.transitioning)
            ctx.currentState[id] = std::string();

        if (!ctx.replaying)
            state->DeleteState(id);
    }

    void setState(sawtooth::GlobalStateUPtr const& state, std::string const& stateData, std::string const& id)
    {
        setState(state.get(), id, stateData);
    }

    void setState(sawtooth::GlobalStateUPtr const& state, std::vector<sawtooth::GlobalState::KeyValue> const& states)
    {
        setState(state.get(), states);
    }

    void deleteState(sawtooth::GlobalStateUPtr const& state, std::string const& id)
    {
        deleteState(state.get(), id);
    }

    std::string getStateData(std::string const& id, bool existing = false)
    {
        return getStateData(state.get(), id, existing);
    }

    std::string getStateData(sawtooth::GlobalState* state, std::string const& id, bool existing = false)
    {
        std::string stateData;

        if (!getState(state , &stateData, id))
        {
            //The section below is commented out because sawtooth 1.0.5 inconsistently returns either key:None or empty list for the same state query
            //      when porting to rust make sure that in sawtooth 1.2 that doesn't happen or doesn't have a special meaning
            //    throw sawtooth::InvalidTransaction("Failed to retrieve the state " + id);
        }

        if (existing && stateData.empty())
        {
            throw sawtooth::InvalidTransaction("Existing state expected " + id);
        }
        return stateData;
    }

    static void addState(std::vector<sawtooth::GlobalState::KeyValue>* states, std::string const& id, google::protobuf::Message const& message)
    {
        std::string data;
        message.SerializeToString(&data);
        states->push_back(sawtooth::GlobalState::KeyValue(id, data));
    }

private:
    void verifyGatewaySigner()
    {
        if (transitioning)
            return;
        const std::string mySighash = getSighash();
        auto actualSettings = settings.load();
        auto setting = actualSettings->find("sawtooth.gateway.sighash");
        if (setting == actualSettings->end())
        {
            doUpdateSettings();
            actualSettings = settings.load();
            setting = actualSettings->find("sawtooth.gateway.sighash");
            if (setting == actualSettings->end())
                throw sawtooth::InvalidTransaction("Gateway sighash is not configured");
        }
        if (mySighash != setting->second)
        {
            throw sawtooth::InvalidTransaction("Only gateway sighash can perform this operation");
        }
    }

    void verify(std::stringstream const& gatewayCommand)
    {
        if (transitioning)
            return;

        std::string response = "";
        try
        {
            std::lock_guard<std::mutex> guard(localGatewayLock);
            if (localGateway->send(gatewayCommand.str()))
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

                auto address = externalGatewayAddress.load();
                if (!address->empty())
                {
                    externalGateway->connect(*address);
                    try
                    {
                        if (externalGateway->send(gatewayCommand.str()))
                        {
                            externalGateway->receive(response);
                        }
                    }
                    catch (...)
                    {
                        externalGateway->disconnect(*address);
                        throw;
                    }
                    externalGateway->disconnect(*address);
                }
            }
        }
        catch (...)
        {
            throw;
        }
        if (response != "good")
        {
            throw sawtooth::InvalidTransaction("Couldn't validate the transaction");
        }
    }

    void award(bool newFormula, boost::multiprecision::cpp_int const& blockIdx, std::string const& signer)
    {
        boost::multiprecision::cpp_int reward;
        std::string rewardString;
        if (newFormula)
        {
            int period = (blockIdx / BLOCKS_IN_PERIOD_UPDATE1).convert_to<int>();
            double fraction = pow(19.0 / 20.0, period);
            std::ostringstream fractionStringBuilder;
            fractionStringBuilder << std::fixed << fraction;
            std::string fractionString = fractionStringBuilder.str();
            size_t pos = fractionString.find('.');
            assert(pos > 0);
            std::ostringstream fractionInWeiStringBuilder;
            if (fractionString[0] != '0')
            {
                fractionInWeiStringBuilder << fractionString.substr(0, pos) << std::left << std::setfill('0') << std::setw(18) << fractionString.substr(pos + 1);
            }
            else
            {
                int pos = 2;
                for (; fractionString[pos] == '0'; ++pos);
                fractionInWeiStringBuilder << std::left << std::setfill('0') << std::setw(20 - pos) << fractionString.substr(pos);
            }
            std::string fractionInWeiString = fractionInWeiStringBuilder.str();
            reward = boost::multiprecision::cpp_int(28) * boost::multiprecision::cpp_int(fractionInWeiString);
            rewardString = toString(reward);
        }
        else
        {
            reward = REWARD_AMOUNT;
            rewardString = REWARD_AMOUNT_STRING;
        }

        if (reward > 0)
        {
            const std::string signerSighash = sha512id(signer);
            const std::string walletId = namespacePrefix + WALLET + signerSighash;
            std::string stateData = getStateData(walletId);
            Wallet wallet;
            if (stateData.empty())
            {
                wallet.set_amount(rewardString);
            }
            else
            {
                wallet.ParseFromString(stateData);
                boost::multiprecision::cpp_int balance = getBigint(wallet.amount());
                balance += reward;
                wallet.set_amount(toString(balance));
            }
            std::vector<sawtooth::GlobalState::KeyValue> states;
            addState(&states, walletId, wallet);
            setState(state, states);
        }
    }

    void reward(boost::multiprecision::cpp_int const& processedBlockIdx, boost::multiprecision::cpp_int const& uptoBlockIdx)
    {
        assert(uptoBlockIdx == 0 || uptoBlockIdx > processedBlockIdx);

        try
        {
            bool newFormula = false;

            if (ctx.transitioning)
            {
                int setBlock = (ctx.tip >= 278890 && ctx.tip <= 278904) ? 277800 : 278910;
                boost::multiprecision::cpp_int updateBlock(setBlock);
                if (updateBlock + 500 < processedBlockIdx)
                    newFormula = true;

                for (boost::multiprecision::cpp_int i = uptoBlockIdx; i > processedBlockIdx; --i)
                {
                    int idx = i.convert_to<int>();
                    SimpleBlock const& block = blocks[idx];

                    award(newFormula, i, block.signer);
                }
                return;
            }

            auto actualSettings = settings.load();
            auto i = actualSettings->find("sawtooth.validator.update1");
            if (i != actualSettings->end())
            {
                std::string updateBlockStr = i->second;
                boost::multiprecision::cpp_int updateBlock = getBigint(updateBlockStr);
                if (updateBlock + 500 < processedBlockIdx)
                    newFormula = true;
            }

            //TODO: use ClientBlockList insted of ClientBlockGetById to retrieve multiple blocks
            //TODO: combine awards for the same miner if possible

            boost::multiprecision::cpp_int const& lastBlockIdx = (uptoBlockIdx == 0) ?
                processedBlockIdx + BLOCK_REWARD_PROCESSING_COUNT :
                uptoBlockIdx;

            const std::string* sig = &txn->block_signature();
            if (sig->empty()) {
                for (boost::multiprecision::cpp_int i = processedBlockIdx + 1; i <= lastBlockIdx; ++i)
                {
                    ::google::protobuf::uint64 height = i.convert_to< ::google::protobuf::uint64>();
                    std::string signer;
                    contextlessState->GetSigByNum(height, &signer);
                    award(newFormula, i, signer);
                }
            } else {

                auto first = lastBlockIdx.convert_to< ::google::protobuf::uint64>();
                auto last = (processedBlockIdx + 1).convert_to< ::google::protobuf::uint64>();
                std::vector<std::string> signatures;
                contextlessState->GetRewardBlockSignatures(*sig,signatures, first, last);

                {
                    boost::multiprecision::cpp_int i = processedBlockIdx;

                    for(auto signature : signatures){
                        award(newFormula, ++i, signature);
                    }
                }
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

    void addFee(std::string const& sighash, std::vector<sawtooth::GlobalState::KeyValue>* states)
    {
        std::string const& guid = getGuid();
        const std::string feeId = makeAddress(FEE, guid);
        Fee fee;
        fee.set_sighash(sighash);
        fee.set_block(lastBlock(ctx));
        addState(states, feeId, fee);
    }

    void addFee(std::string const& sighash, std::vector<sawtooth::GlobalState::KeyValue>* states, std::string const& walletId, Wallet const& wallet)
    {
        addFee(sighash, states);
        addState(states, walletId, wallet);
    }

    std::string charge(std::string const& sighash, Wallet* wallet)
    {
        const std::string walletId = namespacePrefix + WALLET + sighash;
        std::string stateData = getStateData(walletId, true);
        wallet->ParseFromString(stateData);

        boost::multiprecision::cpp_int balance = getBigint(wallet->amount());
        if (balance - TX_FEE < 0)
        {
            throw sawtooth::InvalidTransaction("Insufficient funds");
        }
        wallet->set_amount(toString(balance - TX_FEE));

        return walletId;
    }

    std::string getSighash()
    {
        if (ctx.transitioning)
            return ctx.sighash;
        else
            return sha512id(compress(txn->header()->GetValue(sawtooth::TransactionHeaderField::TransactionHeaderSignerPublicKey)));
    }

    std::string const& getGuid()
    {
        if (ctx.transitioning)
            return ctx.guid;
        else
            return txn->header()->GetValue(sawtooth::TransactionHeaderField::TransactionHeaderNonce);
    }


public: //TODO: tmp, remove 'public', ctx should be private
    Ctx ctx;

private:
    void SendFunds(nlohmann::json const& query)
    {
        boost::multiprecision::cpp_int amount;
        const std::string amountString = getBigint(query, "p1", "amount", &amount);
        const std::string sighash = getStringLower(query, "p2", "sighash");

        const std::string mySighash = getSighash();
        if (sighash == mySighash)
        {
            throw sawtooth::InvalidTransaction("Invalid destination");
        }

        const std::string srcWalletId = namespacePrefix + WALLET + mySighash;
        std::string stateData = getStateData(srcWalletId, true);

        Wallet srcWallet;
        srcWallet.ParseFromString(stateData);
        boost::multiprecision::cpp_int amountPlusTxFee = amount + TX_FEE;
        boost::multiprecision::cpp_int srcBalance = getBigint(srcWallet.amount());
        if (srcBalance < amountPlusTxFee)
        {
            throw sawtooth::InvalidTransaction("Insufficient funds");
        }

        srcBalance -= amountPlusTxFee;
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
        addState(&states, dstWalletId, dstWallet);
        addFee(mySighash, &states, srcWalletId, srcWallet);
        setState(state, states);
    }

    void RegisterAddress(nlohmann::json const& query)
    {
        const std::string blockchain = getStringLower(query, "p1", "blockchain");
        const std::string addressString = getString(query, "p2", "address");
        const std::string network = getStringLower(query, "p3", "network");
        std::string addressStringLower = addressString;
        boost::to_lower(addressStringLower);

        const std::string mySighash = getSighash();

        Wallet wallet;
        std::string walletId = charge(mySighash, &wallet);

        const std::string id = makeAddress(ADDR, blockchain + addressStringLower + network);

        std::string stateData = getStateData(id);
        if (!stateData.empty())
        {
            throw sawtooth::InvalidTransaction("The address has been already registered");
        }

        Address address;
        address.set_blockchain(blockchain);
        address.set_value(addressString);
        address.set_network(network);
        address.set_sighash(mySighash);

        std::vector<sawtooth::GlobalState::KeyValue> states;
        addState(&states, id, address);
        addState(&states, walletId, wallet);
        addFee(mySighash, &states);
        setState(state, states);
    }

    void RegisterTransfer(nlohmann::json const& query)
    {
        const std::string mySighash = getSighash();
        Wallet wallet;
        std::string walletId = charge(mySighash, &wallet);

        std::string gainString = getString(query, "p1", "gain");
        boost::multiprecision::cpp_int gain = getBigint(gainString, true);
        const std::string orderId = getStringLower(query, "p2", "orderId");
        const std::string blockchainTxId = getStringLower(query, "p3", "blockchainTxId");

        std::string srcAddressId;
        std::string dstAddressId;
        std::string amountString;

        std::string stateData = getStateData(orderId, true);
        if (boost::starts_with(orderId, namespacePrefix + DEAL_ORDER))
        {
            DealOrder order;
            order.ParseFromString(stateData);
            if (gain == 0)
            {
                srcAddressId = order.src_address();
                dstAddressId = order.dst_address();
            }
            else
            {
                dstAddressId = order.src_address();
                srcAddressId = order.dst_address();
            }
            amountString = order.amount();
        }
        else if (boost::starts_with(orderId, namespacePrefix + REPAYMENT_ORDER))
        {
            if (gain != 0)
            {
                throw sawtooth::InvalidTransaction("gain must be 0 for repayment orders");
            }
            RepaymentOrder order;
            order.ParseFromString(stateData);
            srcAddressId = order.src_address();
            dstAddressId = order.dst_address();
            amountString = order.amount();
        }
        else
        {
            throw sawtooth::InvalidTransaction("unexpected referred order");
        }

        stateData = getStateData(srcAddressId, true);
        Address srcAddress;
        srcAddress.ParseFromString(stateData);
        stateData = getStateData(dstAddressId, true);
        Address dstAddress;
        dstAddress.ParseFromString(stateData);

        if (srcAddress.sighash() != mySighash)
        {
            throw sawtooth::InvalidTransaction("Only the owner can register");
        }
        const std::string blockchain = srcAddress.blockchain();
        if (dstAddress.blockchain() != blockchain)
        {
            throw sawtooth::InvalidTransaction("Source and destination addresses must be on the same blockchain");
        }

        const std::string network = srcAddress.network();
        if (dstAddress.network() != network)
        {
            throw sawtooth::InvalidTransaction("Source and destination addresses must be on the same network");
        }

        const std::string transferId = makeAddress(TRANSFER, blockchain + blockchainTxId + network);
        stateData = getStateData(transferId);
        if (!stateData.empty())
        {
            throw sawtooth::InvalidTransaction("The transfer has been already registered");
        }

        if (blockchainTxId == "0")
        {
            amountString = "0";
        }
        else
        {
            boost::multiprecision::cpp_int amount = getBigint(amountString);
            amount = amount + gain;
            amountString = toString(amount);

            std::stringstream gatewayCommand;
            gatewayCommand << blockchain << " verify " << srcAddress.value() << " " << dstAddress.value() << " " << orderId << " " << amountString << " " << blockchainTxId << " " << network;
            verify(gatewayCommand);
        }

        Transfer transfer;
        transfer.set_blockchain(blockchain);
        transfer.set_src_address(srcAddressId);
        transfer.set_dst_address(dstAddressId);
        transfer.set_order(orderId);
        transfer.set_amount(amountString);
        transfer.set_tx(blockchainTxId);
        transfer.set_block(lastBlock(ctx));
        transfer.set_processed(false);
        transfer.set_sighash(mySighash);

        std::vector<sawtooth::GlobalState::KeyValue> states;
        addState(&states, transferId, transfer);
        addFee(mySighash, &states, walletId, wallet);
        setState(state, states);
    }

    void AddAskOrder(nlohmann::json const& query)
    {
        const std::string mySighash = getSighash();
        Wallet wallet;
        std::string walletId = charge(mySighash, &wallet);

        const std::string addressId = getStringLower(query, "p1", "addressId");
        const std::string amountString = getBigint(query, "p2", "amount");
        const std::string interest = getBigint(query, "p3", "interest");
        const std::string maturity = getBigint(query, "p4", "maturity");
        const std::string fee = getBigint(query, "p5", "fee");
        const ::google::protobuf::uint64 expiration = getUint64(query, "p6", "expiration");

        std::string const& guid = getGuid();
        const std::string id = makeAddress(ASK_ORDER, guid);
        std::string stateData = getStateData(id);
        if (!stateData.empty())
        {
            throw sawtooth::InvalidTransaction("Duplicate id");
        }

        stateData = getStateData(addressId, true);
        Address address;
        address.ParseFromString(stateData);
        if (address.sighash() != mySighash)
        {
            throw sawtooth::InvalidTransaction("The address doesn't belong to the party");
        }

        AskOrder askOrder;
        askOrder.set_blockchain(address.blockchain());
        askOrder.set_address(addressId);
        askOrder.set_amount(amountString);
        askOrder.set_interest(interest);
        askOrder.set_maturity(maturity);
        askOrder.set_fee(fee);
        askOrder.set_expiration(expiration);
        askOrder.set_block(lastBlock(ctx));
        askOrder.set_sighash(mySighash);

        std::vector<sawtooth::GlobalState::KeyValue> states;
        addState(&states, id, askOrder);
        addFee(mySighash, &states, walletId, wallet);
        setState(state, states);
    }

    void AddBidOrder(nlohmann::json const& query)
    {
        const std::string mySighash = getSighash();
        Wallet wallet;
        std::string walletId = charge(mySighash, &wallet);

        const std::string addressId = getStringLower(query, "p1", "addressId");
        const std::string amountString = getBigint(query, "p2", "amount");
        const std::string interest = getBigint(query, "p3", "interest");
        const std::string maturity = getBigint(query, "p4", "maturity");
        const std::string fee = getBigint(query, "p5", "fee");
        const ::google::protobuf::uint64 expiration = getUint64(query, "p6", "expiration");

        std::string const& guid = getGuid();
        const std::string id = makeAddress(BID_ORDER, guid);
        std::string stateData = getStateData(id);
        if (!stateData.empty())
        {
            throw sawtooth::InvalidTransaction("Duplicate id");
        }

        stateData = getStateData(addressId, true);
        Address address;
        address.ParseFromString(stateData);
        if (address.sighash() != mySighash)
        {
            throw sawtooth::InvalidTransaction("The address doesn't belong to the party");
        }

        BidOrder bidOrder;
        bidOrder.set_blockchain(address.blockchain());
        bidOrder.set_address(addressId);
        bidOrder.set_amount(amountString);
        bidOrder.set_interest(interest);
        bidOrder.set_maturity(maturity);
        bidOrder.set_fee(fee);
        bidOrder.set_expiration(expiration);
        bidOrder.set_block(lastBlock(ctx));
        bidOrder.set_sighash(mySighash);

        std::vector<sawtooth::GlobalState::KeyValue> states;
        addState(&states, id, bidOrder);
        addFee(mySighash, &states, walletId, wallet);
        setState(state, states);
    }

    void AddOffer(nlohmann::json const& query)
    {
        const std::string mySighash = getSighash();
        Wallet wallet;
        std::string walletId = charge(mySighash, &wallet);

        const std::string askOrderId = getStringLower(query, "p1", "askOrderId");
        const std::string bidOrderId = getStringLower(query, "p2", "bidOrderId");
        const ::google::protobuf::uint64 expiration = getUint64(query, "p3", "expiration");

        const std::string id = makeAddress(OFFER, askOrderId + bidOrderId);
        std::string stateData = getStateData(id);
        if (!stateData.empty())
        {
            throw sawtooth::InvalidTransaction("Duplicate id");
        }

        stateData = getStateData(askOrderId, true);
        AskOrder askOrder;
        askOrder.ParseFromString(stateData);
        if (askOrder.sighash() != mySighash)
        {
            throw sawtooth::InvalidTransaction("Only an investor can add an offer");
        }
        boost::multiprecision::cpp_int head = lastBlockInt(ctx);
        boost::multiprecision::cpp_int start = getBigint(askOrder.block());
        boost::multiprecision::cpp_int elapsed = head - start;
        if (askOrder.expiration() < elapsed)
        {
            throw sawtooth::InvalidTransaction("The order has expired");
        }

        stateData = getStateData(askOrder.address(), true);
        Address srcAddress;
        srcAddress.ParseFromString(stateData);

        stateData = getStateData(bidOrderId, true);
        BidOrder bidOrder;
        bidOrder.ParseFromString(stateData);
        if (bidOrder.sighash() == mySighash)
        {
            throw sawtooth::InvalidTransaction("The ask and bid orders are from the same party");
        }
        start = getBigint(bidOrder.block());
        elapsed = head - start;
        if (bidOrder.expiration() < elapsed)
        {
            throw sawtooth::InvalidTransaction("The order has expired");
        }

        stateData = getStateData(bidOrder.address(), true);
        Address dstAddress;
        dstAddress.ParseFromString(stateData);

        if (srcAddress.blockchain() != dstAddress.blockchain() || srcAddress.network() != dstAddress.network())
        {
            throw sawtooth::InvalidTransaction("The ask and bid orders must be on the same blockchain and network");
        }

        if (askOrder.amount() != bidOrder.amount() || askOrder.fee() > bidOrder.fee() || getBigint(askOrder.interest()) / getBigint(askOrder.maturity()) > getBigint(bidOrder.interest()) / getBigint(bidOrder.maturity()))
        {
            throw sawtooth::InvalidTransaction("The ask and bid orders do not match");
        }

        Offer offer;
        offer.set_blockchain(srcAddress.blockchain());
        offer.set_ask_order(askOrderId);
        offer.set_bid_order(bidOrderId);
        offer.set_expiration(expiration);
        offer.set_block(lastBlock(ctx));
        offer.set_sighash(mySighash);

        std::vector<sawtooth::GlobalState::KeyValue> states;
        states.push_back(sawtooth::GlobalState::KeyValue(id, stateData));
        addState(&states, id, offer);
        addFee(mySighash, &states, walletId, wallet);
        setState(state, states);
    }

    void AddDealOrder(nlohmann::json const& query)
    {
        const std::string offerId = getStringLower(query, "p1", "offerId");
        const ::google::protobuf::uint64 expiration = getUint64(query, "p2", "expiration");

        const std::string id = makeAddress(DEAL_ORDER, offerId);
        std::string stateData = getStateData(id);
        if (!stateData.empty())
        {
            throw sawtooth::InvalidTransaction("Duplicate id");
        }

        const std::string mySighash = getSighash();

        stateData = getStateData(offerId, true);
        Offer offer;
        offer.ParseFromString(stateData);
        boost::multiprecision::cpp_int head = lastBlockInt(ctx);
        boost::multiprecision::cpp_int start = getBigint(offer.block());
        boost::multiprecision::cpp_int elapsed = head - start;
        if (offer.expiration() < elapsed)
        {
            throw sawtooth::InvalidTransaction("The order has expired");
        }

        stateData = getStateData(offer.bid_order(), true);
        BidOrder bidOrder;
        bidOrder.ParseFromString(stateData);
        if (bidOrder.sighash() != mySighash)
        {
            throw sawtooth::InvalidTransaction("Only a fundraiser can add a deal order");
        }
        stateData = getStateData(offer.ask_order(), true);
        AskOrder askOrder;
        askOrder.ParseFromString(stateData);

        const std::string walletId = namespacePrefix + WALLET + mySighash;
        stateData = getStateData(walletId, true);

        Wallet wallet;
        wallet.ParseFromString(stateData);
        boost::multiprecision::cpp_int balance = getBigint(wallet.amount());
        boost::multiprecision::cpp_int fee = getBigint(bidOrder.fee()) + TX_FEE;
        if (balance < fee)
        {
            throw sawtooth::InvalidTransaction("Insufficient funds");
        }
        balance -= fee;
        wallet.set_amount(toString(balance));

        DealOrder dealOrder;
        dealOrder.set_blockchain(offer.blockchain());
        dealOrder.set_src_address(askOrder.address());
        dealOrder.set_dst_address(bidOrder.address());
        dealOrder.set_amount(bidOrder.amount());
        dealOrder.set_interest(bidOrder.interest());
        dealOrder.set_maturity(bidOrder.maturity());
        dealOrder.set_fee(bidOrder.fee());
        dealOrder.set_expiration(expiration);
        dealOrder.set_block(lastBlock(ctx));
        dealOrder.set_sighash(mySighash);

        std::vector<sawtooth::GlobalState::KeyValue> states;
        addState(&states, id, dealOrder);
        addFee(mySighash, &states, walletId, wallet);
        setState(state, states);
        deleteState(state, offer.ask_order());
        deleteState(state, offer.bid_order());
        deleteState(state, offerId);
    }

    void CompleteDealOrder(nlohmann::json const& query)
    {
        const std::string dealOrderId = getStringLower(query, "p1", "dealOrderId");
        const std::string transferId = getStringLower(query, "p2", "transferId");

        const std::string mySighash = getSighash();

        std::string stateData = getStateData(dealOrderId, true);
        DealOrder dealOrder;
        dealOrder.ParseFromString(stateData);
        if (!dealOrder.loan_transfer().empty())
        {
            throw sawtooth::InvalidTransaction("The deal has been already completed");
        }

        stateData = getStateData(dealOrder.src_address(), true);
        Address srcAddress;
        srcAddress.ParseFromString(stateData);
        if (srcAddress.sighash() != mySighash)
        {
            throw sawtooth::InvalidTransaction("Only an investor can complete a deal");
        }
        boost::multiprecision::cpp_int head = lastBlockInt(ctx);
        boost::multiprecision::cpp_int start = getBigint(dealOrder.block());
        boost::multiprecision::cpp_int elapsed = head - start;
        if (dealOrder.expiration() < elapsed)
        {
            throw sawtooth::InvalidTransaction("The order has expired");
        }

        stateData = getStateData(transferId, true);
        Transfer transfer;
        transfer.ParseFromString(stateData);

        if (transfer.order() != dealOrderId || transfer.amount() != dealOrder.amount())
        {
            throw sawtooth::InvalidTransaction("The transfer doesn't match the deal order");
        }
        if (transfer.sighash() != mySighash)
        {
            throw sawtooth::InvalidTransaction("The transfer doesn't match the signer");
        }
        if (transfer.processed())
        {
            throw sawtooth::InvalidTransaction("The transfer has been already processed");
        }
        transfer.set_processed(true);

        const std::string walletId = namespacePrefix + WALLET + mySighash;
        stateData = getStateData(walletId, true);

        boost::multiprecision::cpp_int fee = getBigint(dealOrder.fee()) - TX_FEE;

        Wallet wallet;
        if (stateData.empty())
        {
            if (fee < 0)
            {
                throw sawtooth::InvalidTransaction("Insufficient funds");
            }
            wallet.set_amount(toString(fee));
        }
        else
        {
            wallet.ParseFromString(stateData);
            boost::multiprecision::cpp_int balance = getBigint(wallet.amount());
            if (balance + fee < 0)
            {
                throw sawtooth::InvalidTransaction("Insufficient funds");
            }
            balance += fee;
            wallet.set_amount(toString(balance));
        }

        dealOrder.set_loan_transfer(transferId);
        dealOrder.set_block(lastBlock(ctx));

        std::vector<sawtooth::GlobalState::KeyValue> states;
        addState(&states, dealOrderId, dealOrder);
        addState(&states, transferId, transfer);
        addFee(mySighash, &states, walletId, wallet);
        setState(state, states);
    }

    void LockDealOrder(nlohmann::json const& query)
    {
        const std::string mySighash = getSighash();

        Wallet wallet;
        std::string walletId = charge(mySighash, &wallet);

        const std::string dealOrderId = getStringLower(query, "p1", "dealOrderId");

        std::string stateData = getStateData(dealOrderId, true);
        DealOrder dealOrder;
        dealOrder.ParseFromString(stateData);
        if (!dealOrder.lock().empty())
        {
            throw sawtooth::InvalidTransaction("The deal has been already locked");
        }

        if (dealOrder.loan_transfer().empty())
        {
            throw sawtooth::InvalidTransaction("The deal has not been completed yet");
        }

        if (mySighash != dealOrder.sighash())
        {
            throw sawtooth::InvalidTransaction("Only a fundraiser can lock a deal");
        }

        dealOrder.set_lock(mySighash);

        std::vector<sawtooth::GlobalState::KeyValue> states;
        addState(&states, dealOrderId, dealOrder);
        addFee(mySighash, &states, walletId, wallet);
        setState(state, states);
    }

    void CloseDealOrder(nlohmann::json const& query)
    {
        const std::string mySighash = getSighash();
        Wallet wallet;
        std::string walletId = charge(mySighash, &wallet);

        const std::string dealOrderId = getStringLower(query, "p1", "dealOrderId");
        const std::string transferId = getStringLower(query, "p2", "transferId");

        std::string stateData = getStateData(dealOrderId, true);
        DealOrder dealOrder;
        dealOrder.ParseFromString(stateData);
        if (!dealOrder.repayment_transfer().empty())
        {
            throw sawtooth::InvalidTransaction("The deal has been already closed");
        }

        if (mySighash != dealOrder.sighash())
        {
            throw sawtooth::InvalidTransaction("Only a fundraiser can close a deal");
        }

        if (dealOrder.lock() != mySighash)
        {
            throw sawtooth::InvalidTransaction("The deal must be locked first");
        }

        stateData = getStateData(transferId, true);
        Transfer repaymentTransfer;
        repaymentTransfer.ParseFromString(stateData);

        if (repaymentTransfer.order() != dealOrderId)
        {
            throw sawtooth::InvalidTransaction("The transfer doesn't match the order");
        }
        if (repaymentTransfer.sighash() != mySighash)
        {
            throw sawtooth::InvalidTransaction("The transfer doesn't match the signer");
        }
        if (repaymentTransfer.processed())
        {
            throw sawtooth::InvalidTransaction("The transfer has been already processed");
        }
        repaymentTransfer.set_processed(true);

        stateData = getStateData(dealOrder.loan_transfer(), true);
        Transfer loanTransfer;
        loanTransfer.ParseFromString(stateData);

        boost::multiprecision::cpp_int head = lastBlockInt(ctx);
        boost::multiprecision::cpp_int start = getBigint(loanTransfer.block());
        boost::multiprecision::cpp_int maturity = getBigint(dealOrder.maturity());

        boost::multiprecision::cpp_int ticks = ((head - start) + maturity) / maturity;
        boost::multiprecision::cpp_int amount = calcInterest(getBigint(dealOrder.amount()), ticks, getBigint(dealOrder.interest()));
        if (getBigint(repaymentTransfer.amount()) < amount)
        {
            throw sawtooth::InvalidTransaction("The transfer doesn't match the order");
        }

        dealOrder.set_repayment_transfer(transferId);

        std::vector<sawtooth::GlobalState::KeyValue> states;
        addState(&states, dealOrderId, dealOrder);
        addState(&states, transferId, repaymentTransfer);
        addFee(mySighash, &states, walletId, wallet);
        setState(state, states);
    }

    void Exempt(nlohmann::json const& query)
    {
        const std::string mySighash = getSighash();
        Wallet wallet;
        std::string walletId = charge(mySighash, &wallet);

        const std::string dealOrderId = getStringLower(query, "p1", "dealOrderId");
        const std::string transferId = getStringLower(query, "p2", "transferId");

        std::string stateData = getStateData(dealOrderId, true);
        DealOrder dealOrder;
        dealOrder.ParseFromString(stateData);
        if (!dealOrder.repayment_transfer().empty())
        {
            throw sawtooth::InvalidTransaction("The deal has been already closed");
        }

        stateData = getStateData(transferId, true);
        Transfer transfer;
        transfer.ParseFromString(stateData);

        if (transfer.order() != dealOrderId)
        {
            throw sawtooth::InvalidTransaction("The transfer doesn't match the order");
        }
        if (transfer.processed())
        {
            throw sawtooth::InvalidTransaction("The transfer has been already processed");
        }
        transfer.set_processed(true);

        stateData = getStateData(dealOrder.src_address(), true);
        Address address;
        address.ParseFromString(stateData);

        if (mySighash != address.sighash())
        {
            throw sawtooth::InvalidTransaction("Only an investor can exempt a deal");
        }

        dealOrder.set_repayment_transfer(transferId);

        std::vector<sawtooth::GlobalState::KeyValue> states;
        addState(&states, dealOrderId, dealOrder);
        addState(&states, transferId, transfer);
        addFee(mySighash, &states, walletId, wallet);
        setState(state, states);
    }

    void AddRepaymentOrder(nlohmann::json const& query)
    {
        const std::string mySighash = getSighash();
        Wallet wallet;
        std::string walletId = charge(mySighash, &wallet);

        const std::string dealOrderId = getStringLower(query, "p1", "dealOrderId");
        const std::string addressId = getStringLower(query, "p2", "addressId");
        const std::string amount = getBigint(query, "p3", "amount");
        const ::google::protobuf::uint64 expiration = getUint64(query, "p4", "expiration");

        std::string const& guid = getGuid();

        const std::string id = makeAddress(REPAYMENT_ORDER, guid);
        std::string stateData = getStateData(id);
        if (!stateData.empty())
        {
            throw sawtooth::InvalidTransaction("Duplicated id");
        }

        stateData = getStateData(dealOrderId);
        DealOrder dealOrder;
        dealOrder.ParseFromString(stateData);
        if (dealOrder.sighash() == mySighash)
        {
            throw sawtooth::InvalidTransaction("Fundraisers cannot create repayment orders");
        }
        if (dealOrder.loan_transfer().empty() || !dealOrder.repayment_transfer().empty())
        {
            throw sawtooth::InvalidTransaction("A repayment order can be created only for a deal with an active loan");
        }

        stateData = getStateData(dealOrder.src_address());
        Address srcAddress;
        srcAddress.ParseFromString(stateData);
        if (srcAddress.sighash() == mySighash)
        {
            throw sawtooth::InvalidTransaction("Investors cannot create repayment orders");
        }

        stateData = getStateData(addressId);
        Address newAddress;
        newAddress.ParseFromString(stateData);

        if (srcAddress.blockchain() != newAddress.blockchain() || srcAddress.network() != newAddress.network() || srcAddress.value() == newAddress.value())
        {
            throw sawtooth::InvalidTransaction("Invalid address");
        }

        RepaymentOrder repaymentOrder;
        repaymentOrder.set_blockchain(srcAddress.blockchain());
        repaymentOrder.set_src_address(addressId);
        repaymentOrder.set_dst_address(dealOrder.src_address());
        repaymentOrder.set_amount(amount);
        repaymentOrder.set_expiration(expiration);
        repaymentOrder.set_block(lastBlock(ctx));
        repaymentOrder.set_deal(dealOrderId);
        repaymentOrder.set_sighash(mySighash);

        std::vector<sawtooth::GlobalState::KeyValue> states;
        addState(&states, id, repaymentOrder);
        addFee(mySighash, &states, walletId, wallet);
        setState(state, states);
    }

    void CompleteRepaymentOrder(nlohmann::json const& query)
    {
        const std::string mySighash = getSighash();
        Wallet wallet;
        std::string walletId = charge(mySighash, &wallet);

        const std::string repaymentOrderId = getStringLower(query, "p1", "repaymentOrderId");

        std::string stateData = getStateData(repaymentOrderId, true);
        RepaymentOrder repaymentOrder;
        repaymentOrder.ParseFromString(stateData);

        stateData = getStateData(repaymentOrder.dst_address(), true);
        Address address;
        address.ParseFromString(stateData);
        if (address.sighash() != mySighash)
        {
            throw sawtooth::InvalidTransaction("Only an investor can complete a repayment order");
        }

        stateData = getStateData(repaymentOrder.deal(), true);
        DealOrder dealOrder;
        dealOrder.ParseFromString(stateData);
        if (!dealOrder.lock().empty())
        {
            throw sawtooth::InvalidTransaction("The deal has been already locked");
        }

        repaymentOrder.set_previous_owner(mySighash);
        dealOrder.set_lock(mySighash);

        std::vector<sawtooth::GlobalState::KeyValue> states;
        addState(&states, repaymentOrderId, repaymentOrder);
        addState(&states, repaymentOrder.deal(), dealOrder);
        addFee(mySighash, &states, walletId, wallet);
        setState(state, states);
    }

    void CloseRepaymentOrder(nlohmann::json const& query)
    {
        const std::string mySighash = getSighash();
        Wallet wallet;
        std::string walletId = charge(mySighash, &wallet);

        const std::string repaymentOrderId = getStringLower(query, "p1", "repaymentOrderId");
        const std::string transferId = getStringLower(query, "p2", "transferId");

        std::string stateData = getStateData(repaymentOrderId, true);
        RepaymentOrder repaymentOrder;
        repaymentOrder.ParseFromString(stateData);
        if (repaymentOrder.sighash() != mySighash)
        {
            throw sawtooth::InvalidTransaction("Only a collector can close a repayment order");
        }

        stateData = getStateData(transferId, true);
        Transfer transfer;
        transfer.ParseFromString(stateData);

        if (transfer.order() != repaymentOrderId || transfer.amount() != repaymentOrder.amount())
        {
            throw sawtooth::InvalidTransaction("The transfer doesn't match the order");
        }
        if (transfer.sighash() != mySighash)
        {
            throw sawtooth::InvalidTransaction("The transfer doesn't match the signer");
        }
        if (transfer.processed())
        {
            throw sawtooth::InvalidTransaction("The transfer has been already processed");
        }
        transfer.set_processed(true);

        stateData = getStateData(repaymentOrder.deal(), true);
        DealOrder dealOrder;
        dealOrder.ParseFromString(stateData);
        stateData = getStateData(dealOrder.src_address(), true);
        Address srcAddress;
        srcAddress.ParseFromString(stateData);
        if (dealOrder.lock() != srcAddress.sighash())
        {
            throw sawtooth::InvalidTransaction("The deal must be locked");
        }

        dealOrder.set_src_address(repaymentOrder.src_address());
        dealOrder.set_lock("");
        repaymentOrder.set_transfer(transferId);

        std::vector<sawtooth::GlobalState::KeyValue> states;
        addState(&states, repaymentOrderId, repaymentOrder);
        addState(&states, repaymentOrder.deal(), dealOrder);
        addState(&states, transferId, transfer);
        addFee(mySighash, &states, walletId, wallet);
        setState(state, states);
    }

    void CollectCoins(nlohmann::json const& query)
    {
        const std::string ethAddress = getStringLower(query, "p1", "ethAddress");
        boost::multiprecision::cpp_int amount;
        const std::string amountString = getBigint(query, "p2", "amount", &amount);
        const std::string blockchainTxId = getStringLower(query, "p3", "blockchainTxId");

        const std::string id = makeAddress(ERC20, blockchainTxId);
        std::string stateData = getStateData(id);
        if (!stateData.empty())
        {
            throw sawtooth::InvalidTransaction("Already collected");
        }

        const std::string mySighash = getSighash();

        std::stringstream gatewayCommand;
        gatewayCommand << "ethereum verify " << ethAddress << " creditcoin " << mySighash << " " << amountString << " " << blockchainTxId << " unused";
        verify(gatewayCommand);

        const std::string walletId = namespacePrefix + WALLET + mySighash;
        stateData = getStateData(walletId);

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

        std::vector<sawtooth::GlobalState::KeyValue> states;
        addState(&states, walletId, wallet);
        states.push_back(sawtooth::GlobalState::KeyValue(id, amountString));
        setState(state, states);
    }

    void Housekeeping(nlohmann::json const& query)
    {
        boost::multiprecision::cpp_int blockIdx;
        std::string ignore = getBigint(query, "p1", "blockIdx", &blockIdx);

        const std::string processedBlockIdx = namespacePrefix + PROCESSED_BLOCK + PROCESSED_BLOCK_ID;
        std::string stateData = getStateData(processedBlockIdx);
        boost::multiprecision::cpp_int lastProcessedBlockIdx = 0;
        if (!stateData.empty())
        {
            lastProcessedBlockIdx = getBigint(stateData);
        }

        if (blockIdx == 0)
        {
            boost::multiprecision::cpp_int head = lastBlockInt(ctx);
            if (lastProcessedBlockIdx + CONFIRMATION_COUNT * 2 + BLOCK_REWARD_PROCESSING_COUNT < head)
            {
                reward(lastProcessedBlockIdx, 0);
                setState(state, processedBlockIdx, toString(lastProcessedBlockIdx + BLOCK_REWARD_PROCESSING_COUNT));
            }
            return;
        }

        verifyGatewaySigner();

        if (blockIdx < CONFIRMATION_COUNT * 2 || blockIdx <= lastProcessedBlockIdx)
        {
            return;
        }

        boost::multiprecision::cpp_int tip = lastBlockInt(ctx);
        if (blockIdx >= tip - CONFIRMATION_COUNT)
        {
            LOG4CXX_INFO(logger, "Premature processing");
            return;
        }

        Applicator* applicator = this;
        sawtooth::GlobalState* s = state.get();
        filter(ctx, namespacePrefix + ASK_ORDER, [applicator, blockIdx, s](std::string const& address, std::string const& protobuf) {
            AskOrder askOrder;
            askOrder.ParseFromString(protobuf);
            boost::multiprecision::cpp_int start = getBigint(askOrder.block());
            boost::multiprecision::cpp_int elapsed = blockIdx - start;
            if (askOrder.expiration() < elapsed)
            {
                applicator->deleteState(s, address);
            }
        });
        filter(ctx, namespacePrefix + BID_ORDER, [applicator, blockIdx, s](std::string const& address, std::string const& protobuf) {
            BidOrder bidOrder;
            bidOrder.ParseFromString(protobuf);
            boost::multiprecision::cpp_int start = getBigint(bidOrder.block());
            boost::multiprecision::cpp_int elapsed = blockIdx - start;
            if (bidOrder.expiration() < elapsed)
            {
                applicator->deleteState(s, address);
            }
        });
        filter(ctx, namespacePrefix + OFFER, [applicator, blockIdx, s](std::string const& address, std::string const& protobuf) {
            Offer offer;
            offer.ParseFromString(protobuf);
            boost::multiprecision::cpp_int start = getBigint(offer.block());
            boost::multiprecision::cpp_int elapsed = blockIdx - start;
            if (offer.expiration() < elapsed)
            {
                applicator->deleteState(s, address);
            }
        });
        filter(ctx, namespacePrefix + DEAL_ORDER, [applicator, blockIdx, s](std::string const& address, std::string const& protobuf) {
            DealOrder dealOrder;
            dealOrder.ParseFromString(protobuf);
            boost::multiprecision::cpp_int start = getBigint(dealOrder.block());
            boost::multiprecision::cpp_int elapsed = blockIdx - start;
            if (dealOrder.expiration() < elapsed && dealOrder.loan_transfer().empty())
            {
                if (!applicator->ctx.tip || applicator->ctx.tip && applicator->ctx.tip > dealExpFixBlock)
                {
                    const std::string walletId = namespacePrefix + WALLET + dealOrder.sighash();
                    std::string stateData = applicator->getStateData(s, walletId, true);
                    Wallet wallet;
                    wallet.ParseFromString(stateData);
                    boost::multiprecision::cpp_int balance = getBigint(wallet.amount());
                    balance += getBigint(dealOrder.fee());
                    wallet.set_amount(toString(balance));

                    std::vector<sawtooth::GlobalState::KeyValue> states;
                    addState(&states, walletId, wallet);
                    applicator->setState(s, states);
                }
                applicator->deleteState(s, address);
            }
        });
        filter(ctx, namespacePrefix + REPAYMENT_ORDER, [applicator, blockIdx, s](std::string const& address, std::string const& protobuf) {
            RepaymentOrder repaymentOrder;
            repaymentOrder.ParseFromString(protobuf);
            boost::multiprecision::cpp_int start = getBigint(repaymentOrder.block());
            boost::multiprecision::cpp_int elapsed = blockIdx - start;
            if (repaymentOrder.expiration() < elapsed && repaymentOrder.previous_owner().empty())
            {
                applicator->deleteState(s, address);
            }
        });
        filter(ctx, namespacePrefix + FEE, [applicator, blockIdx, s](std::string const& address, std::string const& protobuf) {
            Fee fee;
            fee.ParseFromString(protobuf);
            boost::multiprecision::cpp_int start(fee.block());
            boost::multiprecision::cpp_int elapsed = blockIdx - start;
            if (YEAR_OF_BLOCKS < elapsed)
            {
                const std::string walletId = namespacePrefix + WALLET + fee.sighash();
                std::string stateData;
                if (!applicator->getState(s, &stateData, walletId) || stateData.empty())
                {
                    throw sawtooth::InvalidTransaction("Existing state expected " + walletId);
                }
                Wallet wallet;
                wallet.ParseFromString(stateData);
                wallet.set_amount(toString(getBigint(wallet.amount()) + TX_FEE));

                wallet.SerializeToString(&stateData);
                applicator->setState(s, walletId, stateData);
                applicator->deleteState(s, address);
            }
        });

        reward(lastProcessedBlockIdx, blockIdx);
        setState(state, processedBlockIdx, toString(blockIdx));
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
        return { "1.0", "1.1", "1.2", "1.3", "1.4", "1.5", "1.6", "1.7" };
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
    std::ifstream migrationData(transitionFile);
    if (!static_cast<bool>(migrationData.good()))
    {
        assert(updatingSettings.get() == nullptr);
        updatingSettings.reset(new std::thread(updateSettings));
        updatingSettings->detach();
    }
    else
    {
        transitioning = true;
        for (;;)
        {
            std::string line;
            std::getline(migrationData, line);
            if (line.length() == 0)
                break;
            int blockIdx = getBigint(line).convert_to<int>();
            std::getline(migrationData, line);
            if (blocks.size() <= blockIdx)
            {
                int size = blockIdx + 1;
                blocks.resize(size);
            }
            SimpleBlock b;
            b.signer = line;
            int txCount = 0;
            for (; ; ++txCount)
            {
                std::getline(migrationData, line);
                if (line == ".")
                    break;
                txguid2prevblockidx[line] = TxPos(blockIdx - 1, txCount);
                Tx tx;
                tx.guid = line;
                std::getline(migrationData, line);
                tx.sighash = line;
                std::getline(migrationData, line);
                tx.payload = decodeBase64(line);
                b.txs.push_back(std::move(tx));
            }
            blocks[blockIdx] = std::move(b);
        }
        updatedBlockIdx = 0;
        updatedTxIdx = blocks[updatedBlockIdx].txs.size() - 1;
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
    // console2> [bash]:/# sawset proposal create --url http://rest-api:8008 --key /root/.sawtooth/keys/my_key.priv sawtooth.gateway.sighash="...sighash..."
    // console2> [bash]:/# sawset proposal create --url http://rest-api:8008 --key /root/.sawtooth/keys/my_key.priv sawtooth.validator.transaction_families='[{"family": "creditcoin", "version": "1.0"}, {"family":"sawtooth_settings", "version":"1.0"}]'
    // console2> [bash]:/# sawtooth settings list --url http://rest-api:8008
    // open http://localhost:8008 in a browser
    // -------------------------------------------------------------------------------------------------------
    // console3> ccgateway
    // console4> ccprocessor
    // console5> ccclient creditcoin tmpAddDeal bitcoin mvJr4KdZdx7NzJL87Xx5FNstP1tttbGvq2 10500 bitcoin mp3PRSq1ZKtSDxTqSwWSBLCm3EauHcVD7g 10000
    // console5> ccclient bitcoin registerTransfer 8a1a04aa595e25f63564472cebc9337366bcd4495aee0c50130e0b32975d78313ff35b
    parseArgs(argc, argv);

    zmqpp::context context;
    socketContext = &context;

    int ret = -1;
    try
    {
        localGateway = new zmqpp::socket(context, zmqpp::socket_type::request);
        localGateway->connect(URL_GATEWAY);
        localGateway->set(zmqpp::socket_option::receive_timeout, LOCAL_SOCKET_TIMEOUT_MILLISECONDS);

        externalGateway = new zmqpp::socket(context, zmqpp::socket_type::request);
        externalGateway->set(zmqpp::socket_option::receive_timeout, SOCKET_TIMEOUT_MILLISECONDS);

        log4cxx::BasicConfigurator::configure();

        sawtooth::TransactionProcessorUPtr processor(sawtooth::TransactionProcessor::Create(URL_VALIDATOR));

        contextlessState = processor->CreateContextlessGlobalState();
        setupSettingsAndExternalGatewayAddress();

        sawtooth::TransactionHandlerUPtr transactionHandler(new Handler());
        processor->RegisterHandler(std::move(transactionHandler));

        std::cout << "Running" << std::endl;
        processor->Run();

        ret = 0;
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

    return ret;
}
