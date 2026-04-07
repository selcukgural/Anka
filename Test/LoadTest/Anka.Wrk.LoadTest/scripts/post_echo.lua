-- wrk Lua script for POST /echo scenario.
-- Sends a 256-byte JSON body on every request.

local body = '{"event":"benchmark","payload":"' .. string.rep("x", 220) .. '"}'

request = function()
    return wrk.format("POST", "/echo", {
        ["Content-Type"]   = "application/json",
        ["Content-Length"] = tostring(#body),
    }, body)
end
