FROM mcr.microsoft.com/dotnet/core/runtime:5.0-alpine
COPY ./bin/release/netcoreapp5.0 ./reviewbrahbot

# From https://github.com/wernight/docker-youtube-dl/blob/master/Dockerfile
RUN set -x \
    && apk add --no-cache ca-certificates curl ffmpeg python gnupg \
    && curl -Lo /usr/local/bin/youtube-dl https://yt-dl.org/downloads/latest/youtube-dl \
    && curl -Lo youtube-dl.sig https://yt-dl.org/downloads/latest/youtube-dl.sig \
    && gpg --keyserver keyserver.ubuntu.com --recv-keys '7D33D762FD6C35130481347FDB4B54CBA4826A18' \
    && gpg --keyserver keyserver.ubuntu.com --recv-keys 'ED7F5BF46B3BBED81C87368E2C393E0F18A9236D' \
    && gpg --verify youtube-dl.sig /usr/local/bin/youtube-dl \
    && chmod a+rx /usr/local/bin/youtube-dl \
    && rm youtube-dl.sig \
    && apk del curl gnupg \
    && mkdir /downloads \
    && chmod a+rw /downloads \
    && youtube-dl --version \
    && mkdir /.cache \
    && chmod 777 /.cache

WORKDIR /reviewbrahbot

ENTRYPOINT ["dotnet", "reviewbrahbot.dll"]