using System;
using System.Numerics;
using Microsoft.AspNetCore.Mvc;
using ccbe.Models;
using Microsoft.Extensions.Logging;

namespace ccbe.Controllers
{
    /// <summary>
    /// Blockchain entries
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    public class BlockchainController : Controller
    {
        // GET api/blockchain
        /// <summary>Creditcoin blockchain info</summary>
        /// <returns>A new Blockchain object</returns>
        /// <response code="200">If succeeds</response>
        /// <response code="503">If unable to access Creditcoin network</response>
        [HttpGet]
        [ProducesResponseType(200)]
        [ProducesResponseType(503)]
        public IActionResult Get()
        {
            if (!Cache.IsSuccessful())
                return new StatusCodeResult(503);

            Models.Block tip = Cache.Tip();

            var blockchain = new Blockchain
            {
                BlockHeight = tip.BlockNum,
                Difficulty = tip.Difficulty,
                BlockReward = calculateBlockReward(tip.BlockNum),
                TrnsactionFee = "10000000000000000",
                CirculationSupply = Cache.calculateSupply(),
                NetworkWeight = calculateNetworkWeight(tip.Difficulty)
            };
            return Json(blockchain);
        }


        private static BigInteger[] REWARD_AMOUNT = new BigInteger[]
        {
            BigInteger.Parse("222000000000000000000"),
            BigInteger.Parse("111000000000000000000"),
            BigInteger.Parse( "55500000000000000000"),
            BigInteger.Parse( "27750000000000000000"),
            BigInteger.Parse( "13875000000000000000"),
            BigInteger.Parse(  "6937500000000000000"),
            BigInteger.Parse(  "3468750000000000000"),
            BigInteger.Parse(  "1734375000000000000"),
            BigInteger.Parse(   "867187500000000000"),
            BigInteger.Parse(   "433593750000000000"),
            BigInteger.Parse(   "216796875000000000"),
            BigInteger.Parse(   "108398437500000000"),
            BigInteger.Parse(    "54199218750000000"),
            BigInteger.Parse(    "34179687500000000"),
            BigInteger.Parse(                    "0")
        };

        private static int REWARD_COUNT = REWARD_AMOUNT.Length;

        private const int YEAR_OF_BLOCKS = 60 * 24 * 365;
        private const int BLOCKS_IN_PERIOD = YEAR_OF_BLOCKS * 6;
        private const int REMAINDER_OF_LAST_PERIOD = 2646631;

        internal static string calculateBlockReward(string tipBlockNumStr)
        {
            int schedule = REWARD_COUNT - 1;
            var tipBlockNum = BigInteger.Parse(tipBlockNumStr);

            var period = tipBlockNum / BLOCKS_IN_PERIOD;
            if (period < REWARD_COUNT - 3)
            {
                // for N = 0 to 11 of Nth 6-years periods the reward is 222/2**N
                schedule = (int)period;
            }
            else if (period == REWARD_COUNT - 3)
            {
                // since sum 222/2^N, N = 0 to 11 is equal to 1399856554.6875 coins we have a remainder of 1400000000 - 1399856554.6875 = 143445.3125
                // the reward in 12th period is 222/2**12 = 0.05419921875
                // To pay off the remainder takes (1400000000 - 1399856554.6875)/0.05419921875 = 2646630.63063 blocks
                // which means the reward for 0 to 2646630 blocks of 12th period is 0.05419921875
                // and for 2646631 block is (1400000000 - 1399856554.6875) - 0.05419921875 * 2646630 = 0.0341796875
                var remainder = tipBlockNum % BLOCKS_IN_PERIOD;
                if (remainder == REMAINDER_OF_LAST_PERIOD)
                {
                    schedule = REWARD_COUNT - 2;
                }
                else if (remainder < REMAINDER_OF_LAST_PERIOD)
                {
                    schedule = REWARD_COUNT - 3;
                }
            }

            return REWARD_AMOUNT[schedule].ToString();
        }

        private static string calculateNetworkWeight(string difficultyStr)
        {
            var difficulty = int.Parse(difficultyStr);
            return Math.Pow(2, difficulty).ToString();
        }
    }
}