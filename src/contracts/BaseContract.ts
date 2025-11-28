import { Policy } from "../models/Policy";
import BaseTideRequest from "../models/TideRequest";
import { StringFromUint8Array } from "../utils/Serialization";
import { TideMemory } from "../utils/TideMemory";


export abstract class BaseContract{
    public abstract id: string;
    private tideRequest: BaseTideRequest;
    protected dokens: Doken[] = []; // change to Doken type
    protected authorizedRequestPayload: TideMemory;
    protected informationalRequestPayload: TideMemory;

    /**
     * Inheritors must implement this
     * @param policy Policy object
     */
    protected abstract test(policy: Policy): Promise<void>;

    /**
     * To help with clients testing if their Tide Request will pass their contract's specified contract
     * @param policy Serialized policy from Tide
     * @returns 
     */
    async testPolicy(policy: Uint8Array | Policy): Promise<boolean> {
        const p = policy instanceof Uint8Array ? new Policy(policy) : policy;
        if(p.contractId !== this.id) throw `Mismatch between policy provided's contract (${p.contractId}) and this contract's id (${this.id})`;
        if(p.modelId !== this.tideRequest.id() && p.modelId !== "any") throw `Mismatch between policy provided model id (${p.modelId}) and tide request id (${this.tideRequest.id()})`
        try{
            await this.test(p);
            return true;
        }catch(ex){
            return false;
        }
    }

    constructor(tideRequest: Uint8Array | BaseTideRequest){
        this.tideRequest = tideRequest instanceof Uint8Array ? BaseTideRequest.decode(tideRequest) : tideRequest;
        this.authorizedRequestPayload = this.tideRequest.draft;
        this.informationalRequestPayload = this.tideRequest.dyanmicData;
        
        // deserialize dokens
        let res = {result: new Uint8Array()};
        let i = 0;
        while(this.tideRequest.authorizer.TryGetValue(i, res)){
            this.dokens.push(new Doken(res.result));
            i++;
        }
    }
}




export class Doken{
    private payload: any;
    constructor(d: Uint8Array){
        if(!d || d.length === 0){
            throw new Error('Doken constructor: received empty or null Uint8Array');
        }

        const tokenString = StringFromUint8Array(d);
        const s = tokenString.split(".");

        if(s.length !== 3){
            throw new Error(`Doken constructor: invalid token format. Expected 3 parts (header.payload.signature) but got ${s.length} parts in: "${tokenString.substring(0, 50)}..."`);
        }

        try{
            const decodedPayload = base64UrlDecode(s[1]);
            this.payload = JSON.parse(decodedPayload);
        } catch(error){
            throw new Error(`Doken constructor: failed to parse token payload. ${error instanceof Error ? error.message : String(error)}. Raw payload part: "${s[1].substring(0, 50)}..."`);
        }

        if(!this.payload || typeof this.payload !== 'object'){
            throw new Error(`Doken constructor: parsed payload is not a valid object. Got type: ${typeof this.payload}`);
        }
    }
    hasResourceAccessRole(role: string, client: string): boolean{
        if(!role) throw new Error('hasResourceAccessRole: role parameter is empty or undefined');
        if(!client) throw new Error('hasResourceAccessRole: client parameter is empty or undefined');

        if(!this.payload.resource_access){
            throw new Error(`hasResourceAccessRole: token payload does not contain 'resource_access' field. Available fields: ${Object.keys(this.payload).join(', ')}`);
        }

        if(!this.payload.resource_access[client]){
            throw new Error(`hasResourceAccessRole: client '${client}' not found in resource_access. Available clients: ${Object.keys(this.payload.resource_access).join(', ')}`);
        }

        if(!Array.isArray(this.payload.resource_access[client].roles)){
            throw new Error(`hasResourceAccessRole: 'roles' field for client '${client}' is not an array. Got type: ${typeof this.payload.resource_access[client].roles}`);
        }

        return this.payload.resource_access[client].roles.includes(role);
    }
    hasRealmAccessRole(role: string): boolean{
        if(!role) throw new Error('hasRealmAccessRole: role parameter is empty or undefined');

        if(!this.payload.realm_access){
            throw new Error(`hasRealmAccessRole: token payload does not contain 'realm_access' field. Available fields: ${Object.keys(this.payload).join(', ')}`);
        }

        if(!Array.isArray(this.payload.realm_access.roles)){
            throw new Error(`hasRealmAccessRole: 'roles' field in realm_access is not an array. Got type: ${typeof this.payload.realm_access.roles}`);
        }

        return this.payload.realm_access.roles.includes(role);
    }
    hasVuid(vuid: string): boolean{
        if(!vuid) throw new Error('hasVuid: vuid cannot be null');
        if(!this.payload.vuid) throw new Error("hasVuid: cannot find vuid in paylod");
        return this.payload.vuid === vuid;
    }
}


function base64UrlDecode(input: string) {
    let output = input
        .replaceAll("-", "+")
        .replaceAll("_", "/");

    switch (output.length % 4) {
        case 0:
            break;
        case 2:
            output += "==";
            break;
        case 3:
            output += "=";
            break;
        default:
            throw new Error("Input is not of the correct length.");
    }

    try {
        return b64DecodeUnicode(output);
    } catch (error) {
        return atob(output);
    }
}
function b64DecodeUnicode(input: string) {
    return decodeURIComponent(atob(input).replace(/(.)/g, (m, p) => {
        let code = p.charCodeAt(0).toString(16).toUpperCase();

        if (code.length < 2) {
            code = "0" + code;
        }

        return "%" + code;
    }));
}