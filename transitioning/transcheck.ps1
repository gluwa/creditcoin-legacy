if (-not (Test-Path fail.txt) -and -not (Test-Path ready.txt))
{
    docker cp cctt:/home/Creditcoin/cctt/data/fail.txt . 2>&1 | Out-Null
    docker cp cctt:/home/Creditcoin/cctt/data/done.txt . 2>&1 | Out-Null
    if ((Test-Path fail.txt) -or (Test-Path ready.txt))
    {
        docker-compose --file .\creditcoin-transition.1.2.4.revalidation.yaml stop
        docker-compose --file .\creditcoin-transition.1.2.4.revalidation.yaml down
    }
    if (Test-Path done.txt)
    {
        docker volume rm transitioning_cctt-volume
    }
}

if (Test-Path fail.txt)
{
    Write-Host Failed to revalidate creditcoin.
}
if (Test-Path done.txt)
{
    Write-Host Revalidation completed successfully.
}
