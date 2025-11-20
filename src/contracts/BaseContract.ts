import { Policy } from "../models/Policy";
import BaseTideRequest from "../models/TideRequest";
import { StringFromUint8Array } from "../utils/Serialization";


export abstract class BaseContract{
    public abstract id: string;
    private tideRequest: BaseTideRequest;
    protected dokens: Doken[] = []; // change to Doken type
    protected authorizedRequestPayload: Uint8Array;
    protected informationalRequestPayload: Uint8Array;

    /**
     * Inheritors must implement this
     * @param policy Policy object
     */
    protected abstract test(policy: Policy): void;

    /**
     * To help with clients testing if their Tide Request will pass their contract's specified contract
     * @param policy Serialized policy from Tide
     * @returns 
     */
    testPolicy(policy: Uint8Array): boolean {
        const p = new Policy(policy);
        if(p.contractId !== this.id) throw `Mismatch between policy provided's contract (${p.contractId}) and this contract's id (${this.id})`;
        try{
            this.test(p);
            return true;
        }catch(ex){
            console.error(ex);
            return false;
        }
    }

    constructor(tideRequest: Uint8Array){
        this.tideRequest = BaseTideRequest.decode(tideRequest);
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
        const s = StringFromUint8Array(d).split(".");
        if(s.length != 3) `Expected 3 parts in doken`;
        this.payload = JSON.parse(base64UrlDecode(s[1])); // just payload needed
    }
    hasResourceAccessRole(role: string, client: string): boolean{
        if(!role) throw 'Role is empty';
        if(!client) throw 'Client is empty';
        return this.payload.resource_access[client].roles.includes(role);
    }
    hasRealmAccessRole(role: string): boolean{
        if(!role) throw 'Role is empty';
        return this.payload.realm_access.roles.includes(role);
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