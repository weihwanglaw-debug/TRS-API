IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'UX_Refunds_OrphanActive_GatewaySessionId'
      AND object_id = OBJECT_ID('dbo.Refunds')
)
BEGIN
    CREATE UNIQUE INDEX UX_Refunds_OrphanActive_GatewaySessionId
        ON dbo.Refunds(GatewaySessionId)
        WHERE PaymentID IS NULL
          AND GatewaySessionId IS NOT NULL
          AND RefundStatus IN ('P', 'S');
END;
