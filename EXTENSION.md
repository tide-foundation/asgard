# Extending Asgard

If you're here you must be serious about Asgard and should reach out to the Tide Team at info@tide.org, we'd love to here what you're doing and how we can improve our developer tools for you, the developer. 

## Customizing your Policy Cache
Policies used by Asgard are stored in your Tidecloak instance under your realm. Due to the network factor for fetching these policies, Asgard employs an extendible service for caching your policies named `IAsgardCache`. THe default cache provided in the ASP.NET libraries is `AspDefaultAsgardCache` which simply caches the policies in memory. 

This is obviously not a scalable solution for distributed systems so it's recommended you implement your own cache provider for your distributed system memory store - could be Redis or something else custom.