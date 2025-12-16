# Asgard
Vendor server-side libraries to validate and test Tide Security. 

## Policies
A policy is a data object that contains a set of parameters which validate a specific tide request against a contract.

### Creating a policy for your organization
Any policy you create will create a linkage between either a single Tide Request and a single Contract - or any Tide Request and a single Contract. 

The relationship betweent the contract, policy and tide request is as follows:
1. Contract contains the logic to check policy parameters against a tide request. Contract logic sits on the network's nodes.
2. The policy contains the actual values the contract will check the tide request against. Policies sit on specific applications using the policy (such as a crypto wallet).
3. The tide request contains the policy as part of its payload when sent to the network. Aside from that it is simply a data model.

To create a policy, you'll have to create the Policy object then execute a PolicySignRequest to authorize its use. 

Here's the syntax for it:
```js
const policyParameters = new Map();
policyParameters.set("myNumberParam", 1);
policyParameters.set("myStringParam", "test");
policyParameters.set("myBigIntParam", BigInt(2));
policyParameters.set("myBooleanParam", true);
policyParameters.set("myByteArrayParam", new Uint8Array([0, 2, 1, 3]));

const policy = new Policy({
    version: "1", // default
    modelId: "<model id to use with this policy>",
    contractId: "<contract id to use with this policy>",
    keyId: "<your vendor id>",
    params: policyParameters
});

const policySignRequest = PolicySignRequest.New(policy); // PolicySignRequest will return a single signature of the Policy you added to the PolicySignRequest
const policySignature = // see 7. Executing Tide Requests on tidecloak-js on how to execute a tide request
policy.signature = policySignature;
const policyDataToStore = policy.encode(); // You can now store this signed policy for your client application to use when authorizing tide requests you specified in policy.modelId
```

## Custom Requests

## Basic Contract Test Validation