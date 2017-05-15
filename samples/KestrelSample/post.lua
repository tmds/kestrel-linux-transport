local size = 512



function init(args)

   if args[1] ~= nil then
      size = tonumber(args[1])
   end
   local array = {}
   for i = 1, size do
      array[i] = string.char(math.random(32, 126))
   end
   wrk.body = table.concat(array)
end

wrk.method = "POST"
wrk.headers["Content-Type"] = "application/x-www-form-urlencoded"

