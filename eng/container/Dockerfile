# The elm-service container image (design §20.6, decision 0.111). The release workflow builds it from a context that holds
# the elm-service and LICENSE of the same run's release archives, one folder for each architecture, and an empty data/.
# There is no RUN: the image is the base and those files, nothing built here (T27).
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled@sha256:40eba5db69594d517c3a61e96bdacc2e3d6f8c99a44b52d55edee844d33518d0

ARG TARGETARCH

COPY ${TARGETARCH}/elm-service ${TARGETARCH}/LICENSE /app/
COPY --chown=1654:1654 data/ /data/

# elm-service listens where ELM_LISTEN says and nowhere else; the base's ASP.NET Core port is emptied so that ASP.NET Core
# is told of no other address (design §20.6). ELM_PUBLICURLS and ELM_NODE_NAME are the operator's to set.
ENV ELM_LISTEN=http://0.0.0.0:8080 \
    ASPNETCORE_HTTP_PORTS=

EXPOSE 8080
VOLUME ["/data"]
WORKDIR /data
USER 1654
ENTRYPOINT ["/app/elm-service"]
