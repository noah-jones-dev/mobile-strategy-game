// Flat-shaded, vertex-coloured low-poly shader.
// Lighting is computed per vertex from a single "sun" set through global shader values,
// so it is very cheap on phones and works in both the Built-in and Universal render pipelines.
Shader "Hexfront/Lit"
{
    Properties
    {
        _Color ("Tint", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;
            float4 _HexSunDir;        // world-space direction pointing TO the sun
            fixed4 _HexSunColor;
            fixed4 _HexAmbientSky;
            fixed4 _HexAmbientGround;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                fixed4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                fixed4 color : COLOR0;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                float3 n = UnityObjectToWorldNormal(v.normal);
                float ndl = saturate(dot(n, normalize(_HexSunDir.xyz)));
                fixed3 ambient = lerp(_HexAmbientGround.rgb, _HexAmbientSky.rgb, n.y * 0.5 + 0.5);
                o.color = fixed4(v.color.rgb * _Color.rgb * (ambient + _HexSunColor.rgb * ndl), 1);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                return i.color;
            }
            ENDCG
        }
    }
}
