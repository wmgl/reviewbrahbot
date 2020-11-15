FROM mcr.microsoft.com/dotnet/runtime:5.0-buster-slim
COPY ./bin/release/net5 ./reviewbrahbot

RUN apt-get update && \
    apt-get install -y ffmpeg python python3-pip && \
    pip3 install --upgrade youtube-dl

WORKDIR /reviewbrahbot

ENTRYPOINT ["dotnet", "reviewbrahbot.dll"]