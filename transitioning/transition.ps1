Remove-Item fail.txt 2>&1 | Out-Null
Remove-Item done.txt 2>&1 | Out-Null

docker-compose --file .\creditcoin-transition.1.0.5.collecting.yaml up -d
while (-not (Test-Path done.txt))
{
    docker cp cctt:/home/Creditcoin/cctt/data/done.txt . 2>&1 | Out-Null
    Start-Sleep -s 2
}
docker-compose --file .\creditcoin-transition.1.0.5.collecting.yaml stop
docker-compose --file .\creditcoin-transition.1.0.5.collecting.yaml down

Start-Sleep -s 10

Remove-Item done.txt

docker-compose --file .\creditcoin-transition.1.2.4.revalidation.yaml up
